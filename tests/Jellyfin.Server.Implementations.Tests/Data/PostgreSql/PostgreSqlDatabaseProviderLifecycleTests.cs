using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Providers.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlDatabaseProviderLifecycleTests
{
    private readonly PostgreSqlDatabaseFixture _fixture;

    public PostgreSqlDatabaseProviderLifecycleTests(PostgreSqlDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Maintenance_RefreshesStatisticsAndVacuumWithoutElevatedCommands()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        await using (var context = database.CreateDbContext())
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(true);
            context.ActivityLogs.Add(new ActivityLog("maintenance", "PostgreSqlTest", Guid.Empty));
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        }

        await database.ExecuteNonQueryAsync(
            "CREATE SCHEMA decoy; CREATE TABLE decoy.\"ActivityLogs\" (marker integer) WITH (autovacuum_enabled = false); INSERT INTO decoy.\"ActivityLogs\" VALUES (1)",
            cancellationToken).ConfigureAwait(true);

        var provider = CreateProvider(database);
        await provider.RefreshStatistics(cancellationToken).ConfigureAwait(true);
        await provider.RunScheduledOptimisation(cancellationToken).ConfigureAwait(true);

        Assert.True(await database.ExecuteScalarAsync<bool>(
            "SELECT last_analyze IS NOT NULL AND last_vacuum IS NOT NULL FROM pg_stat_user_tables WHERE schemaname = 'public' AND relname = 'ActivityLogs'",
            cancellationToken).ConfigureAwait(true));
        Assert.True(await database.ExecuteScalarAsync<bool>(
            "SELECT last_analyze IS NULL AND last_vacuum IS NULL FROM pg_stat_user_tables WHERE schemaname = 'decoy' AND relname = 'ActivityLogs'",
            cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task Maintenance_CancelsWhileLockBlockedAndLeavesConnectionsUsable()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        await using var lockContext = database.CreateDbContext();
        await lockContext.Database.MigrateAsync(cancellationToken).ConfigureAwait(true);
        await using var transaction = await lockContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(true);
        await lockContext.Database.ExecuteSqlRawAsync(
            "LOCK TABLE public.\"ActivityLogs\" IN ACCESS EXCLUSIVE MODE",
            cancellationToken).ConfigureAwait(true);

        using var maintenanceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var provider = CreateProvider(database);
        var maintenanceTask = provider.RefreshStatistics(maintenanceCancellation.Token);
        await WaitForBlockedActivityLogLockAsync(database, cancellationToken).ConfigureAwait(true);

        var stopwatch = Stopwatch.StartNew();
        await maintenanceCancellation.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenanceTask).ConfigureAwait(true);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {stopwatch.Elapsed}.");
        Assert.Equal(1, await database.ExecuteScalarAsync<int>("SELECT 1", cancellationToken).ConfigureAwait(true));

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(true);
        await provider.RefreshStatistics(cancellationToken).ConfigureAwait(true);
    }

    [Fact]
    public async Task PurgeDatabase_RemovesRelatedRowsAndRestartsOwnedIdentitySequences()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        var provider = CreateProvider(database);

        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(true);
        var user = new User("purge-user", "test-auth", "test-reset");
        context.Users.Add(user);
        context.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 1, 2, user.Id));
        var firstActivity = new ActivityLog("before purge", "PostgreSqlTest", user.Id);
        context.ActivityLogs.Add(firstActivity);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        Assert.Equal(1, firstActivity.Id);

        var tableNames = context.Model.GetEntityTypes()
            .Select(entityType => entityType.GetSchemaQualifiedTableName())
            .Where(tableName => tableName is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await provider.PurgeDatabase(context, tableNames).ConfigureAwait(true);
        context.ChangeTracker.Clear();

        Assert.Empty(await context.Users.ToListAsync(cancellationToken).ConfigureAwait(true));
        Assert.Empty(await context.AccessSchedules.ToListAsync(cancellationToken).ConfigureAwait(true));
        var afterPurge = new ActivityLog("after purge", "PostgreSqlTest", Guid.Empty);
        context.ActivityLogs.Add(afterPurge);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        Assert.Equal(1, afterPurge.Id);
    }

    [Fact]
    public async Task PurgeDatabase_RejectsUnknownTablesBeforeExecutingSql()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(true);
        context.ActivityLogs.Add(new ActivityLog("preserved", "PostgreSqlTest", Guid.Empty));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider(database).PurgeDatabase(context, ["\"ActivityLogs\"; DROP SCHEMA public CASCADE"]));

        Assert.Contains("not mapped by the Jellyfin database model", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await context.ActivityLogs.CountAsync(cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task PurgeDatabase_DoesNotCascadeOutsideRequestedTables()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(true);
        var user = new User("preserved-user", "test-auth", "test-reset");
        context.Users.Add(user);
        context.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 1, 2, user.Id));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        var userTable = context.Model.FindEntityType(typeof(User))!.GetSchemaQualifiedTableName()!;

        await Assert.ThrowsAsync<PostgresException>(
            () => CreateProvider(database).PurgeDatabase(context, [userTable]));

        context.ChangeTracker.Clear();
        Assert.Equal(1, await context.Users.CountAsync(cancellationToken).ConfigureAwait(true));
        Assert.Equal(1, await context.AccessSchedules.CountAsync(cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task PurgeDatabase_UsesTheMappedSchemaInsteadOfTheConnectionSearchPath()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        await using var context = database.CreateDbContext();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(true);
        context.ActivityLogs.Add(new ActivityLog("public", "PostgreSqlTest", Guid.Empty));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(true);
        await database.ExecuteNonQueryAsync(
            "CREATE SCHEMA decoy; CREATE TABLE decoy.\"ActivityLogs\" (marker integer); INSERT INTO decoy.\"ActivityLogs\" VALUES (1)",
            cancellationToken).ConfigureAwait(true);
        var activityTable = context.Model.FindEntityType(typeof(ActivityLog))!.GetSchemaQualifiedTableName()!;

        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(true);
        await context.Database.ExecuteSqlRawAsync("SET search_path TO decoy, public", cancellationToken).ConfigureAwait(true);
        await CreateProvider(database).PurgeDatabase(context, [activityTable]).ConfigureAwait(true);

        Assert.Equal(0, await database.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM public.\"ActivityLogs\"",
            cancellationToken).ConfigureAwait(true));
        Assert.Equal(1, await database.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM decoy.\"ActivityLogs\"",
            cancellationToken).ConfigureAwait(true));
    }

    private static PostgreSqlDatabaseProvider CreateProvider(PostgreSqlTestDatabase database)
        => new(NullLogger<PostgreSqlDatabaseProvider>.Instance)
        {
            DbContextFactory = new PostgreSqlDbContextFactory(database)
        };

    private static async Task WaitForBlockedActivityLogLockAsync(
        PostgreSqlTestDatabase database,
        CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await database.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM pg_locks WHERE relation = 'public.\"ActivityLogs\"'::regclass AND NOT granted)",
                    cancellationToken).ConfigureAwait(true))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(true);
        }

        throw new TimeoutException("PostgreSQL maintenance did not reach the expected lock wait.");
    }

    private sealed class PostgreSqlDbContextFactory(PostgreSqlTestDatabase database) : IDbContextFactory<JellyfinDbContext>
    {
        public JellyfinDbContext CreateDbContext() => database.CreateDbContext();

        public Task<JellyfinDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
