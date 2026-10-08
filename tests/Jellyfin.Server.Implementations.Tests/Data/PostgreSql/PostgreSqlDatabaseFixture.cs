using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

public sealed class PostgreSqlDatabaseFixture : IAsyncLifetime
{
    public const string ConnectionStringEnvironmentVariable = "JELLYFIN_POSTGRES_TEST_CONNECTION_STRING";
    public const string RuntimeConnectionStringEnvironmentVariable = "JELLYFIN_POSTGRES_TEST_RUNTIME_CONNECTION_STRING";
    public const string RequiredEnvironmentVariable = "JELLYFIN_POSTGRES_TEST_REQUIRED";

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string? _administratorConnectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
    private readonly string? _runtimeConnectionString = Environment.GetEnvironmentVariable(RuntimeConnectionStringEnvironmentVariable);
    private readonly bool _isRequired = string.Equals(
        Environment.GetEnvironmentVariable(RequiredEnvironmentVariable),
        "true",
        StringComparison.OrdinalIgnoreCase);

    private PostgreSqlTestDatabase? _database;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_administratorConnectionString)
        && !string.IsNullOrWhiteSpace(_runtimeConnectionString);

    public string SkipReason => $"Set {ConnectionStringEnvironmentVariable} and {RuntimeConnectionStringEnvironmentVariable} to run tests against a real PostgreSQL server; see Data/PostgreSql/README.md.";

    public ValueTask InitializeAsync()
    {
        if (_isRequired && !IsConfigured)
        {
            throw new InvalidOperationException(
                $"{RequiredEnvironmentVariable}=true requires {ConnectionStringEnvironmentVariable} to be set.");
        }

        return ValueTask.CompletedTask;
    }

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
                _runtimeConnectionString!,
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

    internal Task<PostgreSqlTestDatabase> CreateIsolatedDatabaseAsync(
        CancellationToken cancellationToken,
        Action<string>? databaseCreated = null)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(SkipReason);
        }

        return PostgreSqlTestDatabase.CreateAsync(
            _administratorConnectionString!,
            _runtimeConnectionString!,
            cancellationToken,
            databaseCreated);
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
