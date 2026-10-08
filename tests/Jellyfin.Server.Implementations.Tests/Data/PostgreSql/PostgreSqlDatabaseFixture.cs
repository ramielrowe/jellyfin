using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

public sealed class PostgreSqlDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable = "JELLYFIN_POSTGRES_TEST_CONNECTION_STRING";

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string? _administratorConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
    private PostgreSqlTestDatabase? _database;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_administratorConnectionString);

    public string SkipReason => $"Set {ConnectionStringEnvironmentVariable} to run tests against a real PostgreSQL server; see Data/PostgreSql/README.md.";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    internal async Task<PostgreSqlTestDatabase> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(SkipReason);
        }

        if (_database is not null)
        {
            return _database;
        }

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _database ??= await PostgreSqlTestDatabase.CreateAsync(
                _administratorConnectionString!,
                cancellationToken).ConfigureAwait(false);
            return _database;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    internal Task<bool> DatabaseExistsAsync(string databaseName, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(SkipReason);
        }

        return PostgreSqlTestDatabase.DatabaseExistsAsync(
            _administratorConnectionString!,
            databaseName,
            cancellationToken);
    }

    internal Task<PostgreSqlTestDatabase> CreateIsolatedDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(SkipReason);
        }

        return PostgreSqlTestDatabase.CreateAsync(_administratorConnectionString!, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _initializationLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_database is not null)
            {
                await _database.DisposeAsync().ConfigureAwait(false);
                _database = null;
            }
        }
        finally
        {
            _initializationLock.Release();
            _initializationLock.Dispose();
        }
    }
}
