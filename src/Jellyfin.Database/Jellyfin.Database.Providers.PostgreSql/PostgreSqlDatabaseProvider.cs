using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Providers.PostgreSql.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Jellyfin.Database.Providers.PostgreSql;

/// <summary>
/// Configures Jellyfin to use a PostgreSQL database.
/// </summary>
[JellyfinDatabaseProviderKey(DatabaseProviderKey.PostgreSql)]
public sealed class PostgreSqlDatabaseProvider : IJellyfinDatabaseProvider
{
    private readonly ILogger<PostgreSqlDatabaseProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgreSqlDatabaseProvider"/> class.
    /// </summary>
    /// <param name="logger">A logger.</param>
    public PostgreSqlDatabaseProvider(ILogger<PostgreSqlDatabaseProvider> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public IDbContextFactory<JellyfinDbContext>? DbContextFactory { get; set; }

    /// <inheritdoc/>
    public string ProviderKey => DatabaseProviderKey.PostgreSql;

    /// <inheritdoc/>
    public DatabaseProviderCapabilities Capabilities => DatabaseProviderCapabilities.None;

    /// <inheritdoc/>
    public void Initialise(DbContextOptionsBuilder options, DatabaseConfigurationOptions databaseConfiguration)
    {
        var configuredConnectionString = databaseConfiguration.CustomProviderOptions?.ConnectionString;
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            throw new InvalidOperationException(
                $"Database provider '{DatabaseProviderKey.PostgreSql}' requires a PostgreSQL connection string in database.xml at CustomProviderOptions.ConnectionString.");
        }

        NpgsqlConnectionStringBuilder connectionStringBuilder;
        try
        {
            connectionStringBuilder = new NpgsqlConnectionStringBuilder(configuredConnectionString);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException(
                $"Database provider '{DatabaseProviderKey.PostgreSql}' received an invalid PostgreSQL connection string. Check CustomProviderOptions.ConnectionString.");
        }

        if (string.IsNullOrWhiteSpace(connectionStringBuilder.Host)
            || string.IsNullOrWhiteSpace(connectionStringBuilder.Database))
        {
            throw new InvalidOperationException(
                $"Database provider '{DatabaseProviderKey.PostgreSql}' requires both Host and Database in CustomProviderOptions.ConnectionString.");
        }

        var commandTimeout = GetCommandTimeout(databaseConfiguration.CustomProviderOptions?.Options);

        _logger.LogInformation(
            "PostgreSQL database configured at {Host}:{Port} for database {Database}",
            connectionStringBuilder.Host,
            connectionStringBuilder.Port,
            connectionStringBuilder.Database);

        options.UseNpgsql(
            connectionStringBuilder.ConnectionString,
            postgreSqlOptions =>
            {
                postgreSqlOptions.MigrationsAssembly(GetType().Assembly.GetName().Name!);
                postgreSqlOptions.CommandTimeout(commandTimeout);
            });
    }

    private static int GetCommandTimeout(ICollection<CustomDatabaseOption>? customOptions)
    {
        var configuredTimeout = customOptions?.FirstOrDefault(
            option => option.Key.Equals(PostgreSqlDatabaseProviderOptions.CommandTimeout, StringComparison.OrdinalIgnoreCase));
        if (configuredTimeout is null)
        {
            return PostgreSqlDatabaseProviderOptions.DefaultCommandTimeoutSeconds;
        }

        if (!int.TryParse(configuredTimeout.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var commandTimeout)
            || commandTimeout < PostgreSqlDatabaseProviderOptions.MinimumCommandTimeoutSeconds)
        {
            throw new InvalidOperationException(
                $"Database provider '{DatabaseProviderKey.PostgreSql}' option 'CustomProviderOptions.Options[{PostgreSqlDatabaseProviderOptions.CommandTimeout}]' "
                + $"must be a positive whole number of seconds (minimum {PostgreSqlDatabaseProviderOptions.MinimumCommandTimeoutSeconds}).");
        }

        return commandTimeout;
    }

    /// <inheritdoc/>
    public void OnModelCreating(ModelBuilder modelBuilder)
    {
        var baseItems = modelBuilder.Entity<BaseItemEntity>();
        // These values are application enum/type identifiers, not user-authored metadata. Bounding
        // them retains the selective mixed-column B-trees without constraining titles or paths.
        baseItems.Property(entity => entity.Type).HasMaxLength(512);
        baseItems.Property(entity => entity.MediaType).HasMaxLength(512);

        // PostgreSQL B-tree entries are limited to roughly one third of a page. SQLite accepts
        // arbitrarily long indexed text, so the shared model intentionally leaves these properties
        // unbounded. Keep unique application keys conservatively bounded for PostgreSQL and remove
        // non-unique B-trees which could make an otherwise valid long value impossible to save.
        // The hot single-column equality lookups are restored below as fixed-size hash indexes.
        const int maximumCombinedIndexedCharacters = 512;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var index in entityType.GetIndexes().ToArray())
            {
                var stringProperties = index.Properties
                    .Where(property => property.ClrType == typeof(string))
                    .ToArray();
                if (stringProperties.Length == 0)
                {
                    continue;
                }

                if (index.IsUnique)
                {
                    var maximumPropertyLength = maximumCombinedIndexedCharacters / stringProperties.Length;
                    foreach (var property in stringProperties)
                    {
                        if (!property.GetMaxLength().HasValue || property.GetMaxLength() > maximumPropertyLength)
                        {
                            property.SetMaxLength(maximumPropertyLength);
                        }
                    }
                }
                else if (stringProperties.Any(property => !property.GetMaxLength().HasValue)
                         || stringProperties.Sum(property => property.GetMaxLength()!.Value) > maximumCombinedIndexedCharacters)
                {
                    entityType.RemoveIndex(index);
                }
            }
        }

        baseItems.HasIndex(entity => entity.Path).HasMethod("hash");
        baseItems.HasIndex(entity => entity.Name).HasMethod("hash");
        baseItems.HasIndex(entity => entity.CleanName).HasMethod("hash");
        baseItems.HasIndex(entity => entity.PresentationUniqueKey).HasMethod("hash");
        baseItems.HasIndex(entity => entity.SeriesName).HasMethod("hash");

        // C gives the range and tie-breaker queries a stable ordinal collation, matching SQLite's
        // default BINARY ordering rather than inheriting the PostgreSQL server's locale.
        baseItems.Property(entity => entity.SortName).UseCollation("C");
        baseItems.Property(entity => entity.CleanName).UseCollation("C");
        baseItems.Property(entity => entity.OriginalTitle).UseCollation("C");
        modelBuilder.Entity<People>().Property(entity => entity.Name).UseCollation("C");
    }

    /// <inheritdoc/>
    public void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Jellyfin's persisted DateTime values represent UTC instants. Npgsql maps DateTime to
        // timestamp with time zone and deliberately rejects Local/Unspecified values, so normalize
        // them at the provider boundary while keeping the shared model provider-neutral.
        configurationBuilder.Properties<DateTime>()
            .HaveConversion<UtcDateTimeValueConverter>()
            .HaveColumnType("timestamp with time zone");
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetValueConverter>()
            .HaveColumnType("timestamp with time zone");
    }

    /// <inheritdoc/>
    public Task RunScheduledOptimisation(CancellationToken cancellationToken)
    {
        _logger.LogDebug("PostgreSQL scheduled maintenance is not implemented yet");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RefreshStatistics(CancellationToken cancellationToken)
    {
        _logger.LogDebug("PostgreSQL statistics refresh is not implemented yet");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RunShutdownTask(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<string> MigrationBackupFast(CancellationToken cancellationToken)
        => throw new NotSupportedException("The PostgreSQL provider does not support fast migration backups yet.");

    /// <inheritdoc/>
    public Task RestoreBackupFast(string key, CancellationToken cancellationToken)
        => throw new NotSupportedException("The PostgreSQL provider does not support fast migration backup restores yet.");

    /// <inheritdoc/>
    public Task DeleteBackup(string key)
        => throw new NotSupportedException("The PostgreSQL provider does not support fast migration backup deletion yet.");

    /// <inheritdoc/>
    public Task PurgeDatabase(JellyfinDbContext dbContext, IEnumerable<string>? tableNames)
        => throw new NotSupportedException("The PostgreSQL provider does not support database purges yet.");
}
