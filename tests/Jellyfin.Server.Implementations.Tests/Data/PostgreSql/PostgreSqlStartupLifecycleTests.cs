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
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Helpers;
using Jellyfin.Server.Implementations.DatabaseConfiguration;
using Jellyfin.Server.Implementations.Extensions;
using Jellyfin.Server.Migrations;
using Jellyfin.Server.Migrations.Stages;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        Guid userId;
        int firstActivityId;

        try
        {
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: false);
            await using (var firstStart = await StartProductionHostAsync(paths).ConfigureAwait(true))
            {
                var user = await firstStart.Services.GetRequiredService<IUserManager>()
                    .CreateUserAsync("postgres-lifecycle").ConfigureAwait(true);
                userId = user.Id;
                var activity = new ActivityLog("fresh startup", "PostgreSqlLifecycle", userId) { DateCreated = timestamp };
                await firstStart.Services.GetRequiredService<IActivityManager>().CreateAsync(activity).ConfigureAwait(true);
                firstActivityId = activity.Id;
                Assert.True(firstActivityId > 0);
            }

            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true);
            await using var restart = await StartProductionHostAsync(paths).ConfigureAwait(true);
            Assert.Equal(userId, restart.Services.GetRequiredService<IUserManager>().GetUserById(userId)!.Id);
            var activities = await restart.Services.GetRequiredService<IActivityManager>()
                .GetPagedResultAsync(new ActivityLogQuery { HasUserId = true, Limit = 100 }).ConfigureAwait(true);
            var storedActivity = Assert.Single(activities.Items, item => item.Id == firstActivityId);
            Assert.Equal(timestamp, storedActivity.Date);
            Assert.Equal(userId, storedActivity.UserId);

            var laterActivity = new ActivityLog("after restart", "PostgreSqlLifecycle", userId)
            {
                DateCreated = timestamp.AddMinutes(1)
            };
            await restart.Services.GetRequiredService<IActivityManager>().CreateAsync(laterActivity).ConfigureAwait(true);
            Assert.True(laterActivity.Id > firstActivityId);

            await using var verification = await restart.Services
                .GetRequiredService<IDbContextFactory<JellyfinDbContext>>()
                .CreateDbContextAsync(cancellationToken).ConfigureAwait(true);
            Assert.Equal(verification.Database.GetMigrations(), await GetAppliedProviderMigrationIdsAsync(verification, cancellationToken).ConfigureAwait(true));
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
    public async Task ProductionStartup_AdministratorOwnedDatabaseRunsWithDocumentedRuntimeGrants()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = CreateTemporaryRoot("administrator-owned");
        var paths = CreateApplicationPaths(root);
        string databaseName;

        try
        {
            await using (var database = await _fixture.CreateAdministratorOwnedDatabaseAsync(cancellationToken).ConfigureAwait(true))
            {
                databaseName = database.DatabaseName;
                Assert.True(await database.ExecuteScalarAsync<bool>(
                    """
                    SELECT pg_get_userbyid(datdba) <> current_user
                        AND has_database_privilege(current_user, current_database(), 'CONNECT')
                        AND has_schema_privilege(current_user, 'public', 'USAGE')
                        AND has_schema_privilege(current_user, 'public', 'CREATE')
                    FROM pg_database
                    WHERE datname = current_database()
                    """,
                    cancellationToken).ConfigureAwait(true));

                WriteConfiguration(paths, database.ConnectionString, wizardCompleted: false);
                await using var session = await StartProductionHostAsync(paths).ConfigureAwait(true);
                var activity = new ActivityLog("administrator-owned", "PostgreSqlLifecycle", Guid.Empty);
                await session.Services.GetRequiredService<IActivityManager>().CreateAsync(activity).ConfigureAwait(true);

                var provider = session.Services.GetRequiredService<IJellyfinDatabaseProvider>();
                await provider.RefreshStatistics(cancellationToken).ConfigureAwait(true);
                await provider.RunScheduledOptimisation(cancellationToken).ConfigureAwait(true);

                await using var context = await session.Services
                    .GetRequiredService<IDbContextFactory<JellyfinDbContext>>()
                    .CreateDbContextAsync(cancellationToken).ConfigureAwait(true);
                Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(true));
                Assert.Equal(1, await context.ActivityLogs.CountAsync(cancellationToken).ConfigureAwait(true));
                Assert.True(await database.ExecuteScalarAsync<bool>(
                    "SELECT last_analyze IS NOT NULL AND last_vacuum IS NOT NULL FROM pg_stat_user_tables WHERE schemaname = 'public' AND relname = 'ActivityLogs'",
                    cancellationToken).ConfigureAwait(true));
            }

            Assert.False(await _fixture.DatabaseExistsAsync(databaseName, cancellationToken).ConfigureAwait(true));
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
                    new BaseItemEntity
                    {
                        Id = parentId,
                        Type = typeof(MediaBrowser.Controller.Entities.Folder).FullName!,
                        IsFolder = true,
                        DateCreated = timestamp.AddHours(-2),
                        DateModified = timestamp.AddHours(-1)
                    },
                    new BaseItemEntity
                    {
                        Id = childId,
                        Type = typeof(MediaBrowser.Controller.Entities.TV.Episode).FullName!,
                        ParentId = parentId,
                        DateCreated = timestamp.AddHours(-1),
                        DateModified = timestamp
                    });
                predecessor.ActivityLogs.Add(activity);
                await predecessor.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
            }

            Assert.False(await database.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_ActivityLogs_Type_DateCreated')",
                cancellationToken).ConfigureAwait(true));
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true, targetMigrationId);

            await using (var upgraded = await StartProductionHostAsync(paths).ConfigureAwait(true))
            {
                Assert.Equal(user.Id, upgraded.Services.GetRequiredService<IUserManager>().GetUserById(user.Id)!.Id);
                AssertUpgradedItems(upgraded.Services, parentId, childId, timestamp);
                var activities = await upgraded.Services.GetRequiredService<IActivityManager>()
                    .GetPagedResultAsync(new ActivityLogQuery { HasUserId = true, Limit = 100 }).ConfigureAwait(true);
                var storedActivity = Assert.Single(activities.Items, item => item.Id == activity.Id);
                Assert.Equal(timestamp, storedActivity.Date);
                Assert.Equal(user.Id, storedActivity.UserId);

                await AssertUpgradeHistoryAsync(
                    upgraded.Services,
                    baselineMigrationId,
                    targetMigrationId,
                    cancellationToken).ConfigureAwait(true);
            }

            // A real second production lifecycle rebuilds the provider and every repository/service singleton.
            await using var restarted = await StartProductionHostAsync(paths).ConfigureAwait(true);
            Assert.Equal(user.Id, restarted.Services.GetRequiredService<IUserManager>().GetUserById(user.Id)!.Id);
            AssertUpgradedItems(restarted.Services, parentId, childId, timestamp);
            var laterActivity = new ActivityLog("post-upgrade restart", "PostgreSqlLifecycle", user.Id)
            {
                DateCreated = timestamp.AddDays(1)
            };
            await restarted.Services.GetRequiredService<IActivityManager>().CreateAsync(laterActivity).ConfigureAwait(true);
            Assert.True(laterActivity.Id > activity.Id);

            var afterRestart = await restarted.Services.GetRequiredService<IActivityManager>()
                .GetPagedResultAsync(new ActivityLogQuery { HasUserId = true, Limit = 100 }).ConfigureAwait(true);
            Assert.Contains(afterRestart.Items, item => item.Id == activity.Id && item.UserId.Equals(user.Id) && item.Date == timestamp);
            Assert.Contains(afterRestart.Items, item => item.Id == laterActivity.Id && item.UserId.Equals(user.Id) && item.Date == timestamp.AddDays(1));

            await AssertUpgradeHistoryAsync(
                restarted.Services,
                baselineMigrationId,
                targetMigrationId,
                cancellationToken).ConfigureAwait(true);
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
            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                async () =>
                {
                    await using var ignored = await StartProductionHostAsync(paths).ConfigureAwait(true);
                }).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.BackupRequired, exception.Category);
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
            Assert.False(await database.TableExistsAsync("__EFMigrationsHistory", cancellationToken).ConfigureAwait(true));
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
            Assert.False(await database.TableExistsAsync("__EFMigrationsHistory", cancellationToken).ConfigureAwait(true));
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
            Assert.False(await database.TableExistsAsync("__EFMigrationsHistory", cancellationToken).ConfigureAwait(true));
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProductionStartup_StaleOrWrongBackupAcknowledgementStopsBeforeMutation(bool useStaleAcknowledgement)
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot(useStaleAcknowledgement ? "stale-acknowledgement" : "wrong-acknowledgement");
        var paths = CreateApplicationPaths(root);
        var markerId = Guid.NewGuid();

        try
        {
            string baselineMigrationId;
            await using (var predecessor = database.CreateDbContext())
            {
                var migrations = predecessor.Database.GetMigrations().ToArray();
                baselineMigrationId = migrations[0];
                await predecessor.GetService<IMigrator>().MigrateAsync(baselineMigrationId, cancellationToken).ConfigureAwait(true);
                await SeedAllCodeMigrationHistoryAsync(predecessor, cancellationToken).ConfigureAwait(true);
                predecessor.BaseItems.Add(new BaseItemEntity { Id = markerId, Type = "Movie", Data = "acknowledgement-marker" });
                await predecessor.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
            }

            var historyBefore = await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true);
            WriteConfiguration(
                paths,
                database.ConnectionString,
                wizardCompleted: true,
                useStaleAcknowledgement ? baselineMigrationId : "not-a-supported-migration-target");

            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                async () =>
                {
                    await using var ignored = await StartProductionHostAsync(paths).ConfigureAwait(true);
                }).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.BackupRequired, exception.Category);
            Assert.Equal(historyBefore, await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true));
            await using var verification = database.CreateDbContext();
            Assert.Equal("acknowledgement-marker", (await verification.BaseItems.FindAsync([markerId], cancellationToken).ConfigureAwait(true))!.Data);
            Assert.False(await database.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_ActivityLogs_Type_DateCreated')",
                cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_FutureProviderMigrationHistoryReturnsIncompatibleSchemaWithoutMutation()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        const string FutureMigrationId = "20990101000000_FuturePostgreSqlSchema";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("future-history");
        var paths = CreateApplicationPaths(root);
        var markerId = Guid.NewGuid();

        try
        {
            await using (var predecessor = database.CreateDbContext())
            {
                var baselineMigrationId = predecessor.Database.GetMigrations().First();
                await predecessor.GetService<IMigrator>().MigrateAsync(baselineMigrationId, cancellationToken).ConfigureAwait(true);
                await SeedAllCodeMigrationHistoryAsync(predecessor, cancellationToken).ConfigureAwait(true);
                predecessor.BaseItems.Add(new BaseItemEntity { Id = markerId, Type = "Movie", Data = "future-history-marker" });
                await predecessor.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
                var historyRepository = predecessor.GetService<IHistoryRepository>();
                await predecessor.Database.ExecuteSqlRawAsync(
                    historyRepository.GetInsertScript(new HistoryRow(FutureMigrationId, "future")),
                    cancellationToken).ConfigureAwait(true);
            }

            var historyBefore = await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true);
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true, FutureMigrationId);

            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                () => global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
                    paths,
                    CreateStartupConfiguration(),
                    new StartupOptions())).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.IncompatibleSchema, exception.Category);
            Assert.Contains(FutureMigrationId, exception.Message, StringComparison.Ordinal);
            Assert.Equal(historyBefore, await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true));
            await using var verification = database.CreateDbContext();
            Assert.Equal("future-history-marker", (await verification.BaseItems.FindAsync([markerId], cancellationToken).ConfigureAwait(true))!.Data);
            Assert.False(await database.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_ActivityLogs_Type_DateCreated')",
                cancellationToken).ConfigureAwait(true));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_GappedKnownProviderMigrationHistoryReturnsIncompatibleSchemaWithoutMutation()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("gapped-history");
        var paths = CreateApplicationPaths(root);
        var markerId = Guid.NewGuid();

        try
        {
            string targetMigrationId;
            await using (var predecessor = database.CreateDbContext())
            {
                var migrations = predecessor.Database.GetMigrations().ToArray();
                Assert.Equal(2, migrations.Length);
                var baselineMigrationId = migrations[0];
                targetMigrationId = migrations[1];
                await predecessor.GetService<IMigrator>().MigrateAsync(baselineMigrationId, cancellationToken).ConfigureAwait(true);
                await SeedAllCodeMigrationHistoryAsync(predecessor, cancellationToken).ConfigureAwait(true);
                predecessor.BaseItems.Add(new BaseItemEntity { Id = markerId, Type = typeof(MediaBrowser.Controller.Entities.Movies.Movie).FullName!, Data = "gapped-history-marker" });
                await predecessor.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

                var historyRepository = predecessor.GetService<IHistoryRepository>();
                await predecessor.Database.ExecuteSqlRawAsync(
                    historyRepository.GetDeleteScript(baselineMigrationId),
                    cancellationToken).ConfigureAwait(true);
                await predecessor.Database.ExecuteSqlRawAsync(
                    historyRepository.GetInsertScript(new HistoryRow(targetMigrationId, "gapped")),
                    cancellationToken).ConfigureAwait(true);
            }

            var historyBefore = await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true);
            var schemaBefore = await GetPublicSchemaDefinitionAsync(database, cancellationToken).ConfigureAwait(true);
            var rowCountBefore = await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM \"BaseItems\"", cancellationToken).ConfigureAwait(true);
            var indexBefore = await database.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_ActivityLogs_Type_DateCreated')",
                cancellationToken).ConfigureAwait(true);
            Assert.False(indexBefore);
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true, targetMigrationId);

            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                () => global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
                    paths,
                    CreateStartupConfiguration(),
                    new StartupOptions())).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.IncompatibleSchema, exception.Category);
            Assert.Contains("supported ordered prefix", exception.Message, StringComparison.Ordinal);
            Assert.Equal(historyBefore, await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true));
            Assert.Equal(schemaBefore, await GetPublicSchemaDefinitionAsync(database, cancellationToken).ConfigureAwait(true));
            Assert.Equal(rowCountBefore, await database.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM \"BaseItems\"", cancellationToken).ConfigureAwait(true));
            Assert.Equal(indexBefore, await database.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'IX_ActivityLogs_Type_DateCreated')",
                cancellationToken).ConfigureAwait(true));
            await using var verification = database.CreateDbContext();
            Assert.Equal("gapped-history-marker", (await verification.BaseItems.FindAsync([markerId], cancellationToken).ConfigureAwait(true))!.Data);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProductionStartup_PendingCodeMigrationBackupRequirementUsesBackupRequiredCategoryWithoutMutation()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true);
        var root = CreateTemporaryRoot("code-migration-backup");
        var paths = CreateApplicationPaths(root);
        var markerId = Guid.NewGuid();
        var pendingCodeMigrationId = GetBackupRequiringAppMigrationId();

        try
        {
            await using (var predecessor = database.CreateDbContext())
            {
                await predecessor.Database.MigrateAsync(cancellationToken).ConfigureAwait(true);
                await SeedAllCodeMigrationHistoryAsync(predecessor, cancellationToken, pendingCodeMigrationId).ConfigureAwait(true);
                predecessor.BaseItems.Add(new BaseItemEntity { Id = markerId, Type = "Movie", Data = "code-migration-marker" });
                await predecessor.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
            }

            var historyBefore = await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true);
            WriteConfiguration(paths, database.ConnectionString, wizardCompleted: true);

            var exception = await Assert.ThrowsAsync<DatabaseProviderStartupException>(
                async () =>
                {
                    await using var ignored = await StartProductionHostAsync(paths).ConfigureAwait(true);
                }).ConfigureAwait(true);

            Assert.Equal(DatabaseProviderStartupErrorCategory.BackupRequired, exception.Category);
            Assert.Contains(pendingCodeMigrationId, exception.Message, StringComparison.Ordinal);
            Assert.Equal(historyBefore, await GetAllMigrationIdsAsync(database, cancellationToken).ConfigureAwait(true));
            await using var verification = database.CreateDbContext();
            Assert.Equal("code-migration-marker", (await verification.BaseItems.FindAsync([markerId], cancellationToken).ConfigureAwait(true))!.Data);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task<ProductionHostSession> StartProductionHostAsync(ServerApplicationPaths paths)
    {
        var startupConfiguration = CreateStartupConfiguration();
        var startupOptions = new StartupOptions();
        await StartupHelpers.InitLoggingConfigFile(paths).ConfigureAwait(false);
        await global::Jellyfin.Server.Program.ApplyStartupMigrationAsync(
            paths,
            startupConfiguration,
            startupOptions).ConfigureAwait(false);

        var appHost = new global::Jellyfin.Server.CoreAppHost(
            paths,
            NullLoggerFactory.Instance,
            startupOptions,
            startupConfiguration);
        IHost? host = null;
        try
        {
            host = global::Jellyfin.Server.Program.BuildJellyfinHost(
                appHost,
                paths,
                startupOptions,
                startupConfiguration,
                NullLogger.Instance);
            appHost.ServiceProvider = host.Services;
            await global::Jellyfin.Server.Program.RunCoreStartupLifecycleAsync(
                appHost,
                startupConfiguration,
                NullLogger.Instance).ConfigureAwait(false);
            return new ProductionHostSession(appHost, host);
        }
        catch
        {
            if (appHost.ServiceProvider is not null)
            {
                await global::Jellyfin.Server.Program.ShutdownDatabaseProviderAsync(appHost.ServiceProvider).ConfigureAwait(false);
            }

            host?.Dispose();
            appHost.Dispose();
            throw;
        }
    }

    private static void AssertUpgradedItems(
        IServiceProvider services,
        Guid parentId,
        Guid childId,
        DateTime timestamp)
    {
        var repository = services.GetRequiredService<IItemRepository>();
        var items = repository.GetItems(new InternalItemsQuery
        {
            ItemIds = [parentId, childId],
            EnableTotalRecordCount = true
        });
        Assert.Equal(2, items.TotalRecordCount);
        Assert.Equal(2, items.Items.Count);

        var parent = repository.RetrieveItem(parentId);
        Assert.NotNull(parent);
        Assert.Equal(parentId, parent.Id);
        Assert.Equal(timestamp.AddHours(-2), parent.DateCreated);
        Assert.Equal(timestamp.AddHours(-1), parent.DateModified);

        var child = repository.RetrieveItem(childId);
        Assert.NotNull(child);
        Assert.Equal(childId, child.Id);
        Assert.Equal(parentId, child.ParentId);
        Assert.Equal(timestamp.AddHours(-1), child.DateCreated);
        Assert.Equal(timestamp, child.DateModified);
    }

    private static async Task AssertUpgradeHistoryAsync(
        IServiceProvider services,
        string baselineMigrationId,
        string targetMigrationId,
        CancellationToken cancellationToken)
    {
        await using var context = await services
            .GetRequiredService<IDbContextFactory<JellyfinDbContext>>()
            .CreateDbContextAsync(cancellationToken).ConfigureAwait(true);
        Assert.Equal(
            new[] { baselineMigrationId, targetMigrationId },
            await GetAppliedProviderMigrationIdsAsync(context, cancellationToken).ConfigureAwait(true));
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(true));
        Assert.Contains(
            await context.GetService<IHistoryRepository>().GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(true),
            row => row.MigrationId.EndsWith("_StripEmbeddedLinkedChildren", StringComparison.Ordinal));
    }

    private static async Task SeedAllCodeMigrationHistoryAsync(
        JellyfinDbContext context,
        CancellationToken cancellationToken,
        string? excludedMigrationId = null)
    {
        var historyRepository = context.GetService<IHistoryRepository>();
        var existing = (await historyRepository.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => row.MigrationId)
            .ToHashSet(StringComparer.Ordinal);
        var codeMigrationIds = typeof(JellyfinMigrationService).Assembly.GetTypes()
            .Select(type => (Type: type, Metadata: type.GetCustomAttribute<JellyfinMigrationAttribute>()))
            .Where(item => item.Metadata is not null)
            .Select(item => new CodeMigration(item.Type, item.Metadata!, null).BuildCodeMigrationId())
            .Where(migrationId => !string.Equals(migrationId, excludedMigrationId, StringComparison.Ordinal))
            .Where(migrationId => existing.Add(migrationId));
        foreach (var migrationId in codeMigrationIds)
        {
            await context.Database.ExecuteSqlRawAsync(
                historyRepository.GetInsertScript(new HistoryRow(migrationId, "predecessor")),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetBackupRequiringAppMigrationId()
        => typeof(JellyfinMigrationService).Assembly.GetTypes()
            .Select(type => new
            {
                Type = type,
                Metadata = type.GetCustomAttribute<JellyfinMigrationAttribute>(),
                Backup = type.GetCustomAttributes<JellyfinMigrationBackupAttribute>()
            })
            .Where(item => item.Metadata?.Stage is JellyfinMigrationStageTypes.AppInitialisation && item.Backup.Any(backup => backup.JellyfinDb))
            .Select(item => new CodeMigration(item.Type, item.Metadata!, null).BuildCodeMigrationId())
            .OrderDescending(StringComparer.Ordinal)
            .First();

    private static async Task<string[]> GetAllMigrationIdsAsync(
        PostgreSqlTestDatabase database,
        CancellationToken cancellationToken)
    {
        await using var context = database.CreateDbContext();
        return (await context.GetService<IHistoryRepository>().GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .Select(row => row.MigrationId)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static Task<string> GetPublicSchemaDefinitionAsync(
        PostgreSqlTestDatabase database,
        CancellationToken cancellationToken)
        => database.ExecuteScalarAsync<string>(
            """
            SELECT COALESCE(string_agg(definition, E'\n' ORDER BY definition), '')
            FROM (
                SELECT 'column|' || table_name || '|' || column_name || '|' || data_type || '|' || is_nullable || '|' || COALESCE(column_default, '') AS definition
                FROM information_schema.columns
                WHERE table_schema = 'public'
                UNION ALL
                SELECT 'constraint|' || conname || '|' || pg_get_constraintdef(oid)
                FROM pg_constraint
                WHERE connamespace = 'public'::regnamespace
                UNION ALL
                SELECT 'index|' || indexname || '|' || indexdef
                FROM pg_indexes
                WHERE schemaname = 'public'
            ) AS schema_objects
            """,
            cancellationToken);

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

    private sealed class ProductionHostSession(CoreAppHost appHost, IHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public async ValueTask DisposeAsync()
        {
            await global::Jellyfin.Server.Program.ShutdownDatabaseProviderAsync(host.Services).ConfigureAwait(false);
            host.Dispose();
            appHost.Dispose();
        }
    }
}
