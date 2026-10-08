using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations;
using Emby.Server.Implementations.Configuration;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.DatabaseConfiguration;
using Jellyfin.Server.Implementations.Extensions;
using Jellyfin.Server.Migrations;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

/// <summary>
/// Covers how the migration service treats the database of a server that has been set up before.
/// </summary>
public sealed class JellyfinMigrationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly ServerApplicationPaths _paths;
    private readonly List<ServiceProvider> _serviceProviders = [];

    public JellyfinMigrationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "jellyfin-migration-service-tests", Guid.NewGuid().ToString("N"));
        _paths = new ServerApplicationPaths(
            Path.Combine(_root, "data"),
            Path.Combine(_root, "log"),
            Path.Combine(_root, "config"),
            Path.Combine(_root, "cache"),
            Path.Combine(_root, "web"));
        Directory.CreateDirectory(_paths.DataPath);
        Directory.CreateDirectory(_paths.LogDirectoryPath);
        Directory.CreateDirectory(_paths.ConfigurationDirectoryPath);
        Directory.CreateDirectory(_paths.CachePath);
    }

    private string DatabasePath => Path.Combine(_paths.DataPath, "jellyfin.db");

    [Fact]
    public async Task CheckFirstTimeRunOrMigration_SetUpServerWithoutDatabase_ThrowsWithoutCreatingIt()
    {
        WriteServerConfiguration(wizardCompleted: true);
        var service = CreateService();
        var systemConfiguration = await File.ReadAllBytesAsync(_paths.SystemConfigurationFilePath, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckFirstTimeRunOrMigration(_paths, new StartupOptions()));

        Assert.Contains("the database does not exist", exception.Message, StringComparison.Ordinal);
        Assert.Contains(_paths.SystemConfigurationFilePath, exception.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_paths.DataPath, "jellyfin.db*"));
        Assert.Equal(systemConfiguration, await File.ReadAllBytesAsync(_paths.SystemConfigurationFilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckFirstTimeRunOrMigration_SetUpServerWithEmptyDatabaseFile_Throws()
    {
        WriteServerConfiguration(wizardCompleted: true);
        await File.WriteAllBytesAsync(DatabasePath, [], TestContext.Current.CancellationToken);
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckFirstTimeRunOrMigration(_paths, new StartupOptions()));

        Assert.Contains("the database has no migration history", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckFirstTimeRunOrMigration_SetUpServerWithEmptyHistory_Throws()
    {
        WriteServerConfiguration(wizardCompleted: true);
        var service = CreateService();
        await using (var context = await CreateDbContextAsync())
        {
            await context.GetService<IHistoryRepository>().CreateIfNotExistsAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckFirstTimeRunOrMigration(_paths, new StartupOptions()));

        Assert.Contains("the migration history of the database is empty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckFirstTimeRunOrMigration_SetUpServerWithHistory_Passes()
    {
        WriteServerConfiguration(wizardCompleted: false);
        await CreateService().CheckFirstTimeRunOrMigration(_paths, new StartupOptions());
        var applied = await GetAppliedMigrationIdsAsync();
        WriteServerConfiguration(wizardCompleted: true);

        await CreateService().CheckFirstTimeRunOrMigration(_paths, new StartupOptions());

        Assert.NotEmpty(applied);
        Assert.Equal(applied, await GetAppliedMigrationIdsAsync());
    }

    [Fact]
    public async Task CheckFirstTimeRunOrMigration_SeedSystemOnSetUpServerWithoutDatabase_SeedsCodeMigrations()
    {
        WriteServerConfiguration(wizardCompleted: true);

        await CreateService().CheckFirstTimeRunOrMigration(_paths, new StartupOptions { StartupMode = Configuration.StartupMode.SeedSystem });

        Assert.NotEmpty(await GetAppliedMigrationIdsAsync());
    }

    [Fact]
    public async Task CheckFirstTimeRunOrMigration_MigrateSystemOnSetUpServerWithoutDatabase_Passes()
    {
        WriteServerConfiguration(wizardCompleted: true);

        await CreateService().CheckFirstTimeRunOrMigration(_paths, new StartupOptions { StartupMode = Configuration.StartupMode.MigrateSystem });
    }

    [Fact]
    public async Task CheckFirstTimeRunOrMigration_RestoreArchiveOnSetUpServerWithoutDatabase_Passes()
    {
        WriteServerConfiguration(wizardCompleted: true);

        await CreateService().CheckFirstTimeRunOrMigration(_paths, new StartupOptions { RestoreArchive = Path.Combine(_root, "backup.zip") });
    }

    [Fact]
    public async Task PrepareSystemForMigration_WhenFastBackupIsUnsupported_StopsBeforeBackup()
    {
        var sqliteProvider = new SqliteDatabaseProvider(_paths, NullLogger<SqliteDatabaseProvider>.Instance);
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider.SetupGet(p => p.ProviderKey).Returns(DatabaseProviderKey.PostgreSql);
        provider.SetupGet(p => p.Capabilities).Returns(DatabaseProviderCapabilities.None);
        provider
            .Setup(p => p.Initialise(It.IsAny<DbContextOptionsBuilder>(), It.IsAny<DatabaseConfigurationOptions>()))
            .Callback<DbContextOptionsBuilder, DatabaseConfigurationOptions>(sqliteProvider.Initialise);
        provider.Setup(p => p.OnModelCreating(It.IsAny<ModelBuilder>())).Callback<ModelBuilder>(sqliteProvider.OnModelCreating);
        provider
            .Setup(p => p.ConfigureConventions(It.IsAny<ModelConfigurationBuilder>()))
            .Callback<ModelConfigurationBuilder>(sqliteProvider.ConfigureConventions);
        var service = CreateService(provider.Object);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareSystemForMigration(NullLogger<JellyfinMigrationService>.Instance));

        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not support the fast backup and restore operation", exception.Message, StringComparison.Ordinal);
        provider.Verify(p => p.MigrationBackupFast(It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose()
    {
        foreach (var serviceProvider in _serviceProviders)
        {
            serviceProvider.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best effort, a locked file must not fail the test.
        }
    }

    private void WriteServerConfiguration(bool wizardCompleted)
    {
        new MyXmlSerializer().SerializeToFile(new ServerConfiguration { IsStartupWizardCompleted = wizardCompleted }, _paths.SystemConfigurationFilePath);
    }

    private JellyfinMigrationService CreateService(IJellyfinDatabaseProvider? databaseProvider = null)
    {
        var configurationManager = new ServerConfigurationManager(_paths, NullLoggerFactory.Instance, new MyXmlSerializer());
        configurationManager.AddParts([new DatabaseConfigurationFactory()]);
        var serviceCollection = new ServiceCollection()
            .AddLogging()
            .AddJellyfinDbContext(configurationManager, new ConfigurationBuilder().Build())
            .AddSingleton<IApplicationPaths>(_paths)
            .RegisterStartupLogger();
        if (databaseProvider is not null)
        {
            serviceCollection.AddSingleton(databaseProvider);
        }

        var serviceProvider = serviceCollection.BuildServiceProvider();
        _serviceProviders.Add(serviceProvider);

        var factory = serviceProvider.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        serviceProvider.GetRequiredService<IJellyfinDatabaseProvider>().DbContextFactory = factory;
        return ActivatorUtilities.CreateInstance<JellyfinMigrationService>(serviceProvider);
    }

    private async Task<JellyfinDbContext> CreateDbContextAsync()
    {
        var factory = _serviceProviders[^1].GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        return await factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string[]> GetAppliedMigrationIdsAsync()
    {
        await using var context = await CreateDbContextAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken);
        return applied.Order(StringComparer.Ordinal).ToArray();
    }
}
