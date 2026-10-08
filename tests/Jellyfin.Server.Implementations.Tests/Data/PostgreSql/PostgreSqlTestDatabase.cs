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
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var administratorBuilder = ValidateAdministratorConnectionString(administratorConnectionString);
        administratorBuilder.Pooling = false;
        administratorBuilder.IncludeErrorDetail = false;
        var normalizedAdministratorConnectionString = administratorBuilder.ConnectionString;
        var databaseName = CreateDatabaseName();
        NpgsqlDataSource? administratorDataSource = null;
        NpgsqlDataSource? testDataSource = null;

        try
        {
            administratorDataSource = NpgsqlDataSource.Create(normalizedAdministratorConnectionString);
            await using (var connection = await administratorDataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE DATABASE " + QuoteOwnedDatabaseName(databaseName);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

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
            if (testDataSource is not null)
            {
                await testDataSource.DisposeAsync().ConfigureAwait(false);
            }

            if (administratorDataSource is not null)
            {
                await TryDropOwnedDatabaseAsync(administratorDataSource, databaseName).ConfigureAwait(false);
                await administratorDataSource.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        catch (NpgsqlException)
        {
            if (testDataSource is not null)
            {
                await testDataSource.DisposeAsync().ConfigureAwait(false);
            }

            if (administratorDataSource is not null)
            {
                await TryDropOwnedDatabaseAsync(administratorDataSource, databaseName).ConfigureAwait(false);
                await administratorDataSource.DisposeAsync().ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                "Unable to create an isolated PostgreSQL test database. Verify that the configured server is reachable "
                + "and that the test role has CREATEDB permission.");
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
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public";
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

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS (SELECT FROM pg_tables WHERE schemaname = 'public' AND tablename = $1)";
        command.Parameters.AddWithValue(tableName);
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
        return builder.ConnectionString;
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

    private static async Task TryDropOwnedDatabaseAsync(NpgsqlDataSource administratorDataSource, string databaseName)
    {
        try
        {
            await DropOwnedDatabaseAsync(administratorDataSource, databaseName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // Preserve the original startup or cancellation failure. The generated name is safe to report for manual cleanup.
        }
    }
}
