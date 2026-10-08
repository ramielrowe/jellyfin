using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations;
using Emby.Server.Implementations.Configuration;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.DatabaseConfiguration;
using Jellyfin.Server.Implementations.Extensions;
using Jellyfin.Server.Migrations;
using Jellyfin.Server.Migrations.Stages;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlStartupLifecycleTests
{
    private readonly PostgreSqlDatabaseFixture _fixture;

    public PostgreSqlStartupLifecycleTests(PostgreSqlDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ProductionStartup_FreshDatabasePersistsRepresentativeDataAcrossRestart()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("fresh");
        var paths = CreateApplicationPaths(root);
        var timestamp = new DateTime(2026, 10, 8, 10, 30, 0, DateTimeKind.Utc);
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var user = new User("postgres-lifecycle", "test-auth", "test-reset");
        var activity = new ActivityLog("fresh startup", "PostgreSqlLifecycle", user.Id) { DateCreated = timestamp };

        try
        {
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: false);
            await RunProductionStartupAsync(paths).ConfigureAwait(true);

            await using (var context = database.CreateDbContext())
            {
                context.Users.Add(user);
                context.BaseItems.AddRange(
                    new BaseItemEntity { Id = parentId, Type = "Folder", IsFolder = true },
                    new BaseItemEntity { Id = childId, Type = "Movie", ParentId = parentId });
                context.ActivityLogs.Add(activity);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
                Assert.True(activity.Id > 0);
            }

            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true);
            await RunProductionStartupAsync(paths).ConfigureAwait(true);

            await using var verification = database.CreateDbContext();
            Assert.Equal(2, await verification.BaseItems.CountAsync(
                item => item.Id.Equals(parentId) || item.Id.Equals(childId),
                cancellationToken).ConfigureAwait(true));
            Assert.Equal(parentId, (await verification.BaseItems.SingleAsync(
                item => item.Id.Equals(childId),
                cancellationToken).ConfigureAwait(true)).ParentId);
            Assert.Equal(user.Id, (await verification.Users.SingleAsync(
                item => item.Id.Equals(user.Id),
                cancellationToken).ConfigureAwait(true)).Id);
            var storedActivity = await verification.ActivityLogs.SingleAsync(
                item => item.Id == activity.Id,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(timestamp, storedActivity.DateCreated);
            Assert.Equal(user.Id, storedActivity.UserId);
            Assert.Equal(
                verification.Database.GetMigrations(),
                await GetAppliedProviderMigrationIdsAsync(verification, cancellationToken).ConfigureAwait(true));
            Assert.Contains(
                await verification.GetService<IHistoryRepository>().GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(true),
                row => row.MigrationId.EndsWith("_StripEmbeddedLinkedChildren", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_PredecessorPostgreSqlSchemaUpgradesWithoutDataLoss()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("upgrade");
        var paths = CreateApplicationPaths(root);
        var timestamp = new DateTime(2025, 12, 24, 18, 0, 0, DateTimeKind.Utc);
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var user = new User("postgres-upgrade", "test-auth", "test-reset");
        var activity = new ActivityLog("predecessor row", "PostgreSqlLifecycle", user.Id) { DateCreated = timestamp };
        string baselineMigrationId;
        string targetMigrationId;

        try
        {
            await using (var predecessor = database.CreateDbContext())
            {
                var migrations = predecessor.Database.GetMigrations().ToArray();
                Assert.Equal(2, migrations.Length);
                baselineMigrationId = migrations[0];
                targetMigrationId = migrations[1];
                await predecessor.GetService<IMigrator>().MigrateAsync(baselineMigrationId, cancellationToken).ConfigureAwait(true);
                await SeedAllCodeMigrationHistoryAsync(predecessor, cancellationToken).ConfigureAwait(true);

                predecessor.Users.Add(user);
                predecessor.BaseItems.AddRange(
                    new BaseItemEntity { Id = parentId, Type = "Folder", IsFolder = true },
                    new BaseItemEntity { Id = childId, Type = "Episode", ParentId = parentId });
                predecessor.ActivityLogs.Add(activity);
                await predecessor.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
            }

            Assert.False(await database.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_ActivityLogs_Type_DateCreated')",
                cancellationToken).ConfigureAwait(true));
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true, targetMigrationId);

            await RunProductionStartupAsync(paths).ConfigureAwait(true);

            await using var verification = database.CreateDbContext();
            Assert.Equal(
                new[] { baselineMigrationId, targetMigrationId },
                await GetAppliedProviderMigrationIdsAsync(verification, cancellationToken).ConfigureAwait(true));
            Assert.Empty(await verification.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(true));
            Assert.Equal(parentId, (await verification.BaseItems.SingleAsync(
                item => item.Id.Equals(childId),
                cancellationToken).ConfigureAwait(true)).ParentId);
            Assert.Equal(user.Id, (await verification.Users.SingleAsync(
                item => item.Id.Equals(user.Id),
                cancellationToken).ConfigureAwait(true)).Id);
            var storedActivity = await verification.ActivityLogs.SingleAsync(
                item => item.Id == activity.Id,
                cancellationToken).ConfigureAwait(true);
            Assert.Equal(timestamp, storedActivity.DateCreated);
            Assert.Equal(user.Id, storedActivity.UserId);
            Assert.True(await database.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_ActivityLogs_Type_DateCreated')",
                cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_PendingMigrationWithoutBackupAcknowledgementStopsBeforeMutation()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("backup-required");
        var paths = CreateApplicationPaths(root);
        var markerId = Guid.NewGuid();

        try
        {
            await using (var predecessor = database.CreateDbContext())
            {
                var baseline = predecessor.Database.GetMigrations().First();
                await predecessor.GetService<IMigrator>().MigrateAsync(baseline, cancellationToken).ConfigureAwait(true);
                await SeedAllCodeMigrationHistoryAsync(predecessor, cancellationToken).ConfigureAwait(true);
                predecessor.BaseItems.Add(new BaseItemEntity { Id = markerId, Type = "Movie", Data = "backup-required" });
                await predecessor.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
            }

            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true);
            await global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
                paths,
                CreateStartupConfiguration(),
                new StartupOptions()).ConfigureAwait(true);
            await using var services = CreateCoreMigrationServices(paths);
            var migrationService = ActivatorUtilities.CreateInstance<JellyfinMigrationService>(services);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => migrationService.PrepareSystemForMigration(NullLogger.Instance)).ConfigureAwait(true);

            Assert.Contains(PostgreSqlDatabaseProviderOptions.MigrationBackupAcknowledgement, exception.Message, StringComparison.Ordinal);
            await using var verification = database.CreateDbContext();
            Assert.Single(await GetAppliedProviderMigrationIdsAsync(verification, cancellationToken).ConfigureAwait(true));
            Assert.Equal("backup-required", (await verification.BaseItems.FindAsync([markerId], cancellationToken).ConfigureAwait(true))!.Data);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_WrongCredentialsReturnsRedactedAuthenticationCategory()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        const string Secret = "lifecycle-password-must-not-leak";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("authentication");
        var paths = CreateApplicationPaths(root);
        var connectionString = database.BuildConnectionString(builder =>
        {
            builder.Password = Secret;
            builder.Timeout = 2;
        });

        try
        {
            WriteConfiguration(paths, connectionString, wizardCompleted: false);
            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                () => global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
                    paths,
                    CreateStartupConfiguration(),
                    new StartupOptions())).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.Authentication, exception.Category);
            Assert.Contains("username", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_UnreachableHostReturnsConnectivityCategory()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("connectivity");
        var paths = CreateApplicationPaths(root);
        var connectionString = database.BuildConnectionString(builder =>
        {
            builder.Host = "127.0.0.1";
            builder.Port = 1;
            builder.Timeout = 1;
        });

        try
        {
            WriteConfiguration(paths, connectionString, wizardCompleted: false);
            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                () => global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
                    paths,
                    CreateStartupConfiguration(),
                    new StartupOptions())).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.Connectivity, exception.Category);
            Assert.Contains("host and port", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_InsufficientSchemaPermissionReturnsPermissionCategory()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.DenyRuntimeSchemaAccessAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("permission");
        var paths = CreateApplicationPaths(root);

        try
        {
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: false);
            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                () => global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
                    paths,
                    CreateStartupConfiguration(),
                    new StartupOptions())).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.Permission, exception.Category);
            Assert.Contains("USAGE and CREATE", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_NonEmptyUnknownSchemaReturnsIncompatibleSchemaCategoryWithoutMutation()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ExecuteNonQueryAsync("CREATE TABLE \"UnrelatedApplication\" (\"Id\" integer PRIMARY KEY)", cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("incompatible");
        var paths = CreateApplicationPaths(root);

        try
        {
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: false);
            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                () => global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
                    paths,
                    CreateStartupConfiguration(),
                    new StartupOptions())).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.IncompatibleSchema, exception.Category);
            Assert.Contains("migration history", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Changing from SQLite is not a database migration", exception.Message, StringComparison.Ordinal);
            Assert.True(await database.TableExistsAsync("UnrelatedApplication", cancellationToken).ConfigureAwait(true));
            Assert.False(await database.TableExistsAsync("__EFMigrationsHistory", cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task RunProductionStartupAsync(ServerApplicationPaths paths)
    {
        await global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
            paths,
            CreateStartupConfiguration(),
            new StartupOptions()).ConfigureAwait(false);
        await using var services = CreateCoreMigrationServices(paths);
        var migrationService = ActivatorUtilities.CreateInstance<JellyfinMigrationService>(services);
        await migrationService.PrepareSystemForMigration(NullLogger.Instance).ConfigureAwait(false);
        await migrationService.MigrateStepAsync(JellyfinMigrationStageTypes.CoreInitialisation, services).ConfigureAwait(false);
        await migrationService.CleanupSystemAfterMigration(NullLogger.Instance).ConfigureAwait(false);
    }

    private static ServiceProvider CreateCoreMigrationServices(ServerApplicationPaths paths)
    {
        var configurationManager = new ServerConfigurationManager(paths, NullLoggerFactory.Instance, new MyXmlSerializer());
        configurationManager.AddParts([new DatabaseConfigurationFactory()]);
        var services = new ServiceCollection()
            .AddLogging()
            .AddJellyfinDbContext(configurationManager, CreateStartupConfiguration())
            .AddSingleton<IApplicationPaths>(paths)
            .AddSingleton(paths)
            .RegisterStartupLogger()
            .BuildServiceProvider();
        var factory = services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        services.GetRequiredService<IJellyfinDatabaseProvider>().DbContextFactory = factory;
        return services;
    }

    private static async Task SeedAllCodeMigrationHistoryAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        var historyRepository = context.GetService<IHistoryRepository>();
        var existing = (await historyRepository.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => row.MigrationId)
            .ToHashSet(StringComparer.Ordinal);
        var codeMigrationIds = typeof(JellyfinMigrationService).Assembly.GetTypes()
            .Select(type => (Type: type, Metadata: type.GetCustomAttribute<JellyfinMigrationAttribute>()))
            .Where(item => item.Metadata is not null)
            .Select(item => new CodeMigration(item.Type, item.Metadata!, null).BuildCodeMigrationId())
            .Where(migrationId => existing.Add(migrationId));
        foreach (var migrationId in codeMigrationIds)
        {
            await context.Database.ExecuteSqlRawAsync(
                historyRepository.GetInsertScript(new HistoryRow(migrationId, "predecessor")),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string[]> GetAppliedProviderMigrationIdsAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken)
    {
        var providerMigrationIds = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        return (await context.GetService<IHistoryRepository>()
                .GetAppliedMigrationsAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(row => row.MigrationId)
            .Where(providerMigrationIds.Contains)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static void WriteConfiguration(
        ServerApplicationPaths paths,
        string connectionString,
        bool wizardCompleted,
        string? migrationBackupAcknowledgement = null)
    {
        var serializer = new MyXmlSerializer();
        serializer.SerializeToFile(
            new ServerConfiguration { IsStartupWizardCompleted = wizardCompleted },
            paths.SystemConfigurationFilePath);
        var options = new Collection<CustomDatabaseOption>();
        if (migrationBackupAcknowledgement is not null)
        {
            options.Add(new CustomDatabaseOption
            {
                Key = PostgreSqlDatabaseProviderOptions.MigrationBackupAcknowledgement,
                Value = migrationBackupAcknowledgement
            });
        }

        serializer.SerializeToFile(
            new DatabaseConfigurationOptions
            {
                DatabaseType = DatabaseProviderKey.PostgreSql,
                LockingBehavior = DatabaseLockingBehaviorTypes.NoLock,
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = string.Empty,
                    PluginAssembly = string.Empty,
                    ConnectionString = connectionString,
                    Options = options
                }
            },
            Path.Combine(paths.ConfigurationDirectoryPath, "database.xml"));
    }

    private static IConfiguration CreateStartupConfiguration()
        => new ConfigurationBuilder().Build();

    private static string CreateTemporaryRoot(string scenario)
        => Path.Combine(Path.GetTempPath(), "jellyfin-postgresql-lifecycle-tests", scenario, Guid.NewGuid().ToString("N"));

    private static ServerApplicationPaths CreateApplicationPaths(string root)
    {
        var paths = new ServerApplicationPaths(
            Path.Combine(root, "data"),
            Path.Combine(root, "log"),
            Path.Combine(root, "config"),
            Path.Combine(root, "cache"),
            Path.Combine(root, "web"));
        Directory.CreateDirectory(paths.DataPath);
        Directory.CreateDirectory(paths.LogDirectoryPath);
        Directory.CreateDirectory(paths.ConfigurationDirectoryPath);
        Directory.CreateDirectory(paths.CachePath);
        return paths;
    }
}
