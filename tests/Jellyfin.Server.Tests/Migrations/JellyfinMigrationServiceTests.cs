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
        var provider = CreatePostgreSqlProviderMock(DatabaseProviderCapabilities.None);
        var service = CreateService(provider.Object);

        var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
            () => service.PrepareSystemForMigration(NullLogger<JellyfinMigrationService>.Instance));

        Assert.Equal(DatabaseProviderStartupErrorCategory.UnsupportedBackup, exception.Category);
        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not support the fast backup and restore operation", exception.Message, StringComparison.Ordinal);
        provider.Verify(p => p.MigrationBackupFast(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrepareSystemForMigration_VerifiedFreshEmptyDatabaseDoesNotRequireFastBackup()
    {
        WriteServerConfiguration(wizardCompleted: false);
        await File.WriteAllBytesAsync(DatabasePath, [], TestContext.Current.CancellationToken);
        var provider = CreatePostgreSqlProviderMock(DatabaseProviderCapabilities.None);
        var service = CreateService(provider.Object);

        await service.CheckFirstTimeRunOrMigration(_paths, new StartupOptions());
        await service.PrepareSystemForMigration(NullLogger<JellyfinMigrationService>.Instance);

        provider.Verify(p => p.MigrationBackupFast(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrepareSystemForMigration_PartialSchemaWithOnlyCodeHistoryRequiresFastBackupCapability()
    {
        var provider = CreatePostgreSqlProviderMock(DatabaseProviderCapabilities.None);
        var service = CreateService(provider.Object);
        await using (var context = await CreateDbContextAsync())
        {
            var historyRepository = context.GetService<IHistoryRepository>();
            await historyRepository.CreateIfNotExistsAsync(TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                "CREATE TABLE \"PartialApplicationSchema\" (\"Id\" INTEGER NOT NULL PRIMARY KEY)",
                TestContext.Current.CancellationToken);
            await context.Database.ExecuteSqlRawAsync(
                historyRepository.GetInsertScript(new HistoryRow("202601010000000_ExistingCodeMigration", "test")),
                TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
            () => service.PrepareSystemForMigration(NullLogger<JellyfinMigrationService>.Instance));

        Assert.Equal(DatabaseProviderStartupErrorCategory.UnsupportedBackup, exception.Category);
        Assert.Contains("does not support the fast backup and restore operation", exception.Message, StringComparison.Ordinal);
        provider.Verify(p => p.MigrationBackupFast(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PrepareSystemForMigration_WithOldStyleProvider_UsesLegacyFastBackup()
    {
        var provider = new LegacyDatabaseProvider(_paths, throwFromBackup: false);
        Assert.Equal(DatabaseProviderCapabilities.Unknown, ((IJellyfinDatabaseProvider)provider).Capabilities);
        var service = CreateService(provider);

        await service.PrepareSystemForMigration(NullLogger<JellyfinMigrationService>.Instance);

        Assert.True(provider.MigrationBackupCalled);
    }

    [Fact]
    public async Task PrepareSystemForMigration_WithOldStyleProviderNotImplementingFastBackup_StopsSafely()
    {
        var provider = new LegacyDatabaseProvider(_paths, throwFromBackup: true);
        var service = CreateService(provider);

        var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
            () => service.PrepareSystemForMigration(NullLogger<JellyfinMigrationService>.Instance));

        Assert.Equal(DatabaseProviderStartupErrorCategory.UnsupportedBackup, exception.Category);
        Assert.True(provider.MigrationBackupCalled);
        Assert.Contains(((IJellyfinDatabaseProvider)provider).ProviderKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("does not implement the fast backup operation", exception.Message, StringComparison.Ordinal);
        Assert.IsType<NotImplementedException>(exception.InnerException);
    }

    [Fact]
    public async Task TryRestoreJellyfinDatabaseBackup_WhenProviderSucceeds_ReturnsTrue()
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider
            .Setup(p => p.TryRestoreBackupFast("backup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DatabaseProviderOperationResult.Success());

        var result = await JellyfinMigrationService.TryRestoreJellyfinDatabaseBackup(
            provider.Object,
            "backup",
            NullLogger.Instance);

        Assert.True(result);
    }

    [Fact]
    public async Task TryRestoreJellyfinDatabaseBackup_WhenProviderReportsFailure_ReturnsFalse()
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider
            .Setup(p => p.TryRestoreBackupFast("backup", It.IsAny<CancellationToken>()))
            .ReturnsAsync(DatabaseProviderOperationResult.Failure("restore failed"));

        var result = await JellyfinMigrationService.TryRestoreJellyfinDatabaseBackup(
            provider.Object,
            "backup",
            NullLogger.Instance);

        Assert.False(result);
    }

    [Fact]
    public async Task TryRestoreJellyfinDatabaseBackup_WhenProviderThrows_ReturnsFalse()
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider
            .Setup(p => p.TryRestoreBackupFast("backup", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("restore failed"));

        var result = await JellyfinMigrationService.TryRestoreJellyfinDatabaseBackup(
            provider.Object,
            "backup",
            NullLogger.Instance);

        Assert.False(result);
    }

    [Fact]
    public async Task TryDeleteJellyfinDatabaseBackup_WhenProviderSucceeds_ReturnsTrue()
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider
            .Setup(p => p.TryDeleteBackup("backup"))
            .ReturnsAsync(DatabaseProviderOperationResult.Success());

        var result = await JellyfinMigrationService.TryDeleteJellyfinDatabaseBackup(
            provider.Object,
            "backup",
            NullLogger.Instance);

        Assert.True(result);
    }

    [Fact]
    public async Task TryDeleteJellyfinDatabaseBackup_WhenProviderReportsFailure_ReturnsFalse()
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider
            .Setup(p => p.TryDeleteBackup("backup"))
            .ReturnsAsync(DatabaseProviderOperationResult.Failure("delete failed"));

        var result = await JellyfinMigrationService.TryDeleteJellyfinDatabaseBackup(
            provider.Object,
            "backup",
            NullLogger.Instance);

        Assert.False(result);
    }

    [Fact]
    public async Task TryDeleteJellyfinDatabaseBackup_WhenProviderThrows_ReturnsFalse()
    {
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider
            .Setup(p => p.TryDeleteBackup("backup"))
            .ThrowsAsync(new IOException("delete failed"));

        var result = await JellyfinMigrationService.TryDeleteJellyfinDatabaseBackup(
            provider.Object,
            "backup",
            NullLogger.Instance);

        Assert.False(result);
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

    private Mock<IJellyfinDatabaseProvider> CreatePostgreSqlProviderMock(DatabaseProviderCapabilities capabilities)
    {
        var sqliteProvider = new SqliteDatabaseProvider(_paths, NullLogger<SqliteDatabaseProvider>.Instance);
        var provider = new Mock<IJellyfinDatabaseProvider>();
        provider.SetupGet(p => p.ProviderKey).Returns(DatabaseProviderKey.PostgreSql);
        provider.SetupGet(p => p.Capabilities).Returns(capabilities);
        provider
            .Setup(p => p.Initialise(It.IsAny<DbContextOptionsBuilder>(), It.IsAny<DatabaseConfigurationOptions>()))
            .Callback<DbContextOptionsBuilder, DatabaseConfigurationOptions>(sqliteProvider.Initialise);
        provider.Setup(p => p.OnModelCreating(It.IsAny<ModelBuilder>())).Callback<ModelBuilder>(sqliteProvider.OnModelCreating);
        provider
            .Setup(p => p.ConfigureConventions(It.IsAny<ModelConfigurationBuilder>()))
            .Callback<ModelConfigurationBuilder>(sqliteProvider.ConfigureConventions);
        return provider;
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

    private sealed class LegacyDatabaseProvider : IJellyfinDatabaseProvider
    {
        private readonly SqliteDatabaseProvider _inner;
        private readonly bool _throwFromBackup;

        public LegacyDatabaseProvider(IApplicationPaths applicationPaths, bool throwFromBackup)
        {
            _inner = new SqliteDatabaseProvider(applicationPaths, NullLogger<SqliteDatabaseProvider>.Instance);
            _throwFromBackup = throwFromBackup;
        }

        public bool MigrationBackupCalled { get; private set; }

        public IDbContextFactory<JellyfinDbContext>? DbContextFactory
        {
            get => _inner.DbContextFactory;
            set => _inner.DbContextFactory = value;
        }

        public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
            => _inner.Initialise(options, databaseConfiguration);

        public void OnModelCreating(ModelBuilder modelBuilder) => _inner.OnModelCreating(modelBuilder);

        public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
            => _inner.ConfigureConventions(configurationBuilder);

        public Task RunScheduledOptimisation(CancellationToken cancellationToken)
            => _inner.RunScheduledOptimisation(cancellationToken);

        public Task RunShutdownTask(CancellationToken cancellationToken)
            => _inner.RunShutdownTask(cancellationToken);

        public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
        {
            MigrationBackupCalled = true;
            return _throwFromBackup
                ? throw new NotImplementedException()
                : Task.FromResult("legacy-backup");
        }

        public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task DeleteBackup(string key) => throw new NotImplementedException();

        public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
            => _inner.PurgeDatabase(dbContext, tableNames);
    }
}
