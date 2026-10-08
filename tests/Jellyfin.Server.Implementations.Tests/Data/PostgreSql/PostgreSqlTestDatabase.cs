using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

internal sealed class PostgreSqlTestDatabase : IAsyncDisposable
{
    internal const string DatabaseNamePrefix = "jellyfin_test_";
    internal const string RequiredAdministratorDatabaseMarker = "jellyfin_test";

    private readonly string _administratorConnectionString;
    private readonly string _connectionString;
    private readonly NpgsqlDataSource _dataSource;
    private bool _disposed;

    private PostgreSqlTestDatabase(
        string administratorConnectionString,
        string connectionString,
        string databaseName,
        NpgsqlDataSource dataSource)
    {
        _administratorConnectionString = administratorConnectionString;
        _connectionString = connectionString;
        DatabaseName = databaseName;
        _dataSource = dataSource;
    }

    public string DatabaseName { get; }

    public string SanitizedConnectionString => RedactConnectionString(_connectionString);

    public static async Task<PostgreSqlTestDatabase> CreateAsync(
        string administratorConnectionString,
        CancellationToken cancellationToken,
        Action<string>? databaseCreated = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var administratorBuilder = ValidateAdministratorConnectionString(administratorConnectionString);
        administratorBuilder.Pooling = false;
        administratorBuilder.IncludeErrorDetail = false;
        var normalizedAdministratorConnectionString = administratorBuilder.ConnectionString;
        var databaseName = CreateDatabaseName();
        NpgsqlDataSource? administratorDataSource = null;
        NpgsqlDataSource? testDataSource = null;
        var createDatabaseAttempted = false;

        try
        {
            administratorDataSource = NpgsqlDataSource.Create(normalizedAdministratorConnectionString);
            await using (var connection = await administratorDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT NOT rolsuper AND NOT rolcreaterole AND rolcreatedb FROM pg_roles WHERE rolname = current_user";
                if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
                {
                    throw new InvalidOperationException(
                        "The PostgreSQL integration-test role must be NOSUPERUSER, NOCREATEROLE, and CREATEDB; see Data/PostgreSql/README.md.");
                }

                command.CommandText = "CREATE DATABASE " + QuoteOwnedDatabaseName(databaseName);
                createDatabaseAttempted = true;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            databaseCreated?.Invoke(databaseName);
            cancellationToken.ThrowIfCancellationRequested();

            var testBuilder = new NpgsqlConnectionStringBuilder(normalizedAdministratorConnectionString)
            {
                Database = databaseName,
                ApplicationName = "Jellyfin PostgreSQL integration tests",
                Pooling = true
            };
            var testConnectionString = testBuilder.ConnectionString;
            testDataSource = NpgsqlDataSource.Create(testConnectionString);

            await using (var connection = await testDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            {
                // Opening the connection verifies that CREATE DATABASE completed and the isolated database is usable.
            }

            await administratorDataSource.DisposeAsync().ConfigureAwait(false);
            return new PostgreSqlTestDatabase(
                normalizedAdministratorConnectionString,
                testConnectionString,
                databaseName,
                testDataSource);
        }
        catch (OperationCanceledException)
        {
            if (!await CleanupFailedCreationAsync(
                    testDataSource,
                    administratorDataSource,
                    databaseName,
                    createDatabaseAttempted).ConfigureAwait(false)
                && createDatabaseAttempted)
            {
                throw CreateCleanupFailureException(databaseName);
            }

            throw;
        }
        catch (NpgsqlException)
        {
            if (!await CleanupFailedCreationAsync(
                    testDataSource,
                    administratorDataSource,
                    databaseName,
                    createDatabaseAttempted).ConfigureAwait(false)
                && createDatabaseAttempted)
            {
                throw CreateCleanupFailureException(databaseName);
            }

            throw new InvalidOperationException(
                "Unable to create an isolated PostgreSQL test database. Verify that the configured server is reachable "
                + "and that the test role has CREATEDB permission.");
        }
        catch (Exception)
        {
            if (!await CleanupFailedCreationAsync(
                    testDataSource,
                    administratorDataSource,
                    databaseName,
                    createDatabaseAttempted).ConfigureAwait(false)
                && createDatabaseAttempted)
            {
                throw CreateCleanupFailureException(databaseName);
            }

            throw;
        }
    }

    public JellyfinDbContext CreateDbContext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var provider = new PostgreSqlDatabaseProvider(NullLogger<PostgreSqlDatabaseProvider>.Instance);
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        provider.Initialise(
            optionsBuilder,
            new DatabaseConfigurationOptions
            {
                DatabaseType = DatabaseProviderKey.PostgreSql,
                CustomProviderOptions = new CustomDatabaseOptions
                {
                    PluginName = string.Empty,
                    PluginAssembly = string.Empty,
                    ConnectionString = _connectionString
                }
            });

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _ = QuoteOwnedDatabaseName(DatabaseName);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureConnectedToOwnedDatabaseAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DO $reset$
            DECLARE schema_name text;
            BEGIN
                FOR schema_name IN
                    SELECT nspname
                    FROM pg_namespace
                    WHERE nspname <> 'information_schema'
                        AND nspname !~ '^pg_'
                LOOP
                    EXECUTE format('DROP SCHEMA %I CASCADE', schema_name);
                END LOOP;
            END
            $reset$;
            CREATE SCHEMA public;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ExecuteNonQueryAsync(string commandText, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> ExecuteScalarAsync<T>(string commandText, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (T)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_tables WHERE schemaname = 'public' AND tablename = $1)";
        command.Parameters.AddWithValue(tableName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<bool> SchemaExistsAsync(string schemaName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_namespace WHERE nspname = $1)";
        command.Parameters.AddWithValue(schemaName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public static string CreateDatabaseName()
        => DatabaseNamePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public static bool IsOwnedDatabaseName(string databaseName)
        => databaseName.StartsWith(DatabaseNamePrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(databaseName.AsSpan(DatabaseNamePrefix.Length), "N", out _);

    public static string RedactConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        _ = builder.Remove("Password");
        _ = builder.Remove("Passfile");
        _ = builder.Remove("SSL Password");
        return builder.ConnectionString;
    }

    internal static InvalidOperationException CreateCleanupFailureException(string databaseName)
    {
        _ = QuoteOwnedDatabaseName(databaseName);
        return new InvalidOperationException(
            $"Unable to remove isolated PostgreSQL test database '{databaseName}' after test setup failed. "
            + "Remove that database manually before rerunning the tests.");
    }

    public static NpgsqlConnectionStringBuilder ValidateAdministratorConnectionString(string connectionString)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string is invalid. Check "
                + PostgreSqlDatabaseFixture.ConnectionStringEnvironmentVariable + ".");
        }

        if (string.IsNullOrWhiteSpace(builder.Host)
            || string.IsNullOrWhiteSpace(builder.Database)
            || string.IsNullOrWhiteSpace(builder.Username))
        {
            throw new InvalidOperationException(
                "The PostgreSQL integration-test connection string must specify Host, Database, and Username.");
        }

        if (!builder.Database.Contains(RequiredAdministratorDatabaseMarker, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The configured administrator database name must contain the safety marker '{RequiredAdministratorDatabaseMarker}'.");
        }

        return builder;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _dataSource.DisposeAsync().ConfigureAwait(false);

        await using var administratorDataSource = NpgsqlDataSource.Create(_administratorConnectionString);
        try
        {
            await DropOwnedDatabaseAsync(administratorDataSource, DatabaseName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            throw new InvalidOperationException(
                $"Unable to remove isolated PostgreSQL test database '{DatabaseName}'. Remove that database manually before rerunning the tests.");
        }

        GC.SuppressFinalize(this);
    }

    internal static async Task<bool> DatabaseExistsAsync(
        string administratorConnectionString,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var builder = ValidateAdministratorConnectionString(administratorConnectionString);
        builder.Pooling = false;
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_database WHERE datname = $1)";
        command.Parameters.AddWithValue(databaseName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static string QuoteOwnedDatabaseName(string databaseName)
    {
        if (!IsOwnedDatabaseName(databaseName))
        {
            throw new InvalidOperationException("Refusing to operate on a database not created by the PostgreSQL test fixture.");
        }

        return '"' + databaseName + '"';
    }

    private static async Task DropOwnedDatabaseAsync(
        NpgsqlDataSource administratorDataSource,
        string databaseName,
        CancellationToken cancellationToken)
    {
        await using var connection = await administratorDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP DATABASE IF EXISTS " + QuoteOwnedDatabaseName(databaseName) + " WITH (FORCE)";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureConnectedToOwnedDatabaseAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database()";
        var currentDatabase = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        if (!IsOwnedDatabaseName(currentDatabase)
            || !string.Equals(currentDatabase, DatabaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to reset a database not created by this PostgreSQL test fixture.");
        }
    }

    private static async Task<bool> CleanupFailedCreationAsync(
        NpgsqlDataSource? testDataSource,
        NpgsqlDataSource? administratorDataSource,
        string databaseName,
        bool createDatabaseAttempted)
    {
        var cleanupSucceeded = true;

        try
        {
            if (testDataSource is not null)
            {
                await testDataSource.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            cleanupSucceeded = false;
        }

        if (administratorDataSource is not null)
        {
            if (createDatabaseAttempted
                && !await TryDropOwnedDatabaseAsync(administratorDataSource, databaseName).ConfigureAwait(false))
            {
                cleanupSucceeded = false;
            }

            try
            {
                await administratorDataSource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                cleanupSucceeded = false;
            }
        }

        return cleanupSucceeded;
    }

    private static async Task<bool> TryDropOwnedDatabaseAsync(NpgsqlDataSource administratorDataSource, string databaseName)
    {
        try
        {
            await DropOwnedDatabaseAsync(administratorDataSource, databaseName, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
