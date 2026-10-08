using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlIntegrationTests
{
    private readonly PostgreSqlDatabaseFixture _fixture;

    public PostgreSqlIntegrationTests(PostgreSqlDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task JellyfinDbContext_OpensNpgsqlConnection()
    {
        SkipUnlessConfigured();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);

        await using var context = database.CreateDbContext();
        await context.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(true);

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        Assert.Equal(database.DatabaseName, context.Database.GetDbConnection().Database);
        Assert.DoesNotContain("Password", database.SanitizedConnectionString, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResetAsync_RemovesObjectsFromOwnedDatabase()
    {
        SkipUnlessConfigured();
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        await database.ExecuteNonQueryAsync("CREATE TABLE fixture_reset_probe (id integer PRIMARY KEY)", cancellationToken).ConfigureAwait(true);
        await database.ExecuteNonQueryAsync(
            "CREATE SCHEMA fixture_private; CREATE TABLE fixture_private.fixture_reset_probe (id integer PRIMARY KEY)",
            cancellationToken).ConfigureAwait(true);

        Assert.True(await database.TableExistsAsync("fixture_reset_probe", cancellationToken).ConfigureAwait(true));
        Assert.True(await database.SchemaExistsAsync("fixture_private", cancellationToken).ConfigureAwait(true));

        await database.ResetAsync(cancellationToken).ConfigureAwait(true);

        Assert.False(await database.TableExistsAsync("fixture_reset_probe", cancellationToken).ConfigureAwait(true));
        Assert.False(await database.SchemaExistsAsync("fixture_private", cancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task CreateAsync_CancellationAfterDatabaseCreationDropsDatabase()
    {
        SkipUnlessConfigured();
        using var cancellationTokenSource = new CancellationTokenSource();
        string? databaseName = null;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _fixture.CreateIsolatedDatabaseAsync(
                cancellationTokenSource.Token,
                createdDatabaseName =>
                {
                    databaseName = createdDatabaseName;
                    cancellationTokenSource.Cancel();
                })).ConfigureAwait(true);

        Assert.NotNull(databaseName);
        Assert.True(PostgreSqlTestDatabase.IsOwnedDatabaseName(databaseName));
        Assert.False(await _fixture.DatabaseExistsAsync(databaseName, TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Fact]
    public async Task DisposeAsync_DropsOnlyCreatedDatabase()
    {
        SkipUnlessConfigured();
        var cancellationToken = TestContext.Current.CancellationToken;
        string databaseName;
        await using (var database = await _fixture.CreateIsolatedDatabaseAsync(cancellationToken).ConfigureAwait(true))
        {
            databaseName = database.DatabaseName;

            Assert.True(PostgreSqlTestDatabase.IsOwnedDatabaseName(databaseName));
            Assert.True(await _fixture.DatabaseExistsAsync(databaseName, cancellationToken).ConfigureAwait(true));
        }

        Assert.False(await _fixture.DatabaseExistsAsync(databaseName, cancellationToken).ConfigureAwait(true));
    }

    private void SkipUnlessConfigured()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
    }
}
