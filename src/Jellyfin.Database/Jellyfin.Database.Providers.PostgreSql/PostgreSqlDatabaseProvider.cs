using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Providers.PostgreSql.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
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
        // These values are application type discriminators, not user-authored metadata. Bounding
        // them retains the selective mixed-column B-trees without constraining names or paths.
        baseItems.Property(entity => entity.Type).HasMaxLength(512);
        baseItems.Property(entity => entity.MediaType).HasMaxLength(64);

        // PostgreSQL B-tree entries are limited to roughly one third of a page. The presentation,
        // sort, name, and path values below are intentionally unbounded because some are derived
        // from user metadata. Remove only the B-trees which contain those values; all numeric,
        // UUID, date, and bounded-discriminator indexes configured by the shared model remain.
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.SeriesPresentationUniqueKey), nameof(BaseItemEntity.PresentationUniqueKey), nameof(BaseItemEntity.SortName));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.SeriesPresentationUniqueKey), nameof(BaseItemEntity.IsFolder), nameof(BaseItemEntity.IsVirtualItem));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.PresentationUniqueKey));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.PresentationUniqueKey), nameof(BaseItemEntity.DateCreated));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.IsFolder), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.PresentationUniqueKey), nameof(BaseItemEntity.DateCreated));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.MediaType), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.PresentationUniqueKey));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.SortName));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.SeriesPresentationUniqueKey), nameof(BaseItemEntity.ParentIndexNumber), nameof(BaseItemEntity.IndexNumber));
        RemoveIndex(baseItems.Metadata, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.CleanName));

        // Equality predicates on unbounded text are safe and useful as hash indexes. PostgreSQL can
        // combine these with the retained B-trees using bitmap scans for series, suggestions,
        // resume, Next Up, and by-name queries.
        baseItems.HasIndex(entity => entity.Path).HasMethod("hash");
        baseItems.HasIndex(entity => entity.Name).HasMethod("hash");
        baseItems.HasIndex(entity => entity.CleanName).HasMethod("hash");
        baseItems.HasIndex(entity => entity.PresentationUniqueKey).HasMethod("hash");
        baseItems.HasIndex(entity => entity.SeriesPresentationUniqueKey).HasMethod("hash");
        baseItems.HasIndex(entity => entity.SeriesName).HasMethod("hash");
        baseItems.HasIndex(entity => entity.SortName).HasMethod("hash");

        // These are the bounded/range/order portions of the removed composites. Their leading
        // columns match the corresponding shared query predicates; the hash indexes above provide
        // the unbounded equality portions where required. SortName cannot safely participate in a
        // PostgreSQL B-tree without limiting valid metadata, so sorted-library queries use the
        // Type/TopParentId selector and perform their final sort on the selected rows.
        baseItems.HasIndex(entity => new { entity.Type, entity.TopParentId });
        baseItems.HasIndex(entity => new { entity.Type, entity.IsFolder, entity.IsVirtualItem });
        baseItems.HasIndex(entity => new { entity.MediaType, entity.TopParentId, entity.IsVirtualItem });
        baseItems.HasIndex(entity => new { entity.Type, entity.ParentIndexNumber, entity.IndexNumber });

        ConfigurePeopleIndexes(modelBuilder);
        ConfigureMediaStreamIndexes(modelBuilder);
        ConfigureDeviceIndexes(modelBuilder);
        ConfigureProviderIdIndexes(modelBuilder);
        ConfigureUnboundedCompositeKeys(modelBuilder);
        ConfigureItemValueIndexes(modelBuilder);
        ConfigureCustomPreferenceIndexes(modelBuilder);

        // C gives the range and tie-breaker queries a stable ordinal collation, matching SQLite's
        // default BINARY ordering rather than inheriting the PostgreSQL server's locale.
        baseItems.Property(entity => entity.SortName).UseCollation("C");
        baseItems.Property(entity => entity.CleanName).UseCollation("C");
        baseItems.Property(entity => entity.OriginalTitle).UseCollation("C");
        modelBuilder.Entity<People>().Property(entity => entity.Name).UseCollation("C");

        var activityLogs = modelBuilder.Entity<ActivityLog>();
        activityLogs.Property(entity => entity.Name).UseCollation("C");
        activityLogs.Property(entity => entity.Overview).UseCollation("C");
        activityLogs.Property(entity => entity.ShortOverview).UseCollation("C");
        activityLogs.Property(entity => entity.Type).UseCollation("C");

        var users = modelBuilder.Entity<User>();
        users.Property(entity => entity.Username).UseCollation("C");
        users.Property(entity => entity.NormalizedUsername).UseCollation("C");
    }

    private static void ConfigurePeopleIndexes(ModelBuilder modelBuilder)
    {
        var people = modelBuilder.Entity<People>();
        RemoveIndex(people.Metadata, nameof(People.Name));
        people.HasIndex(entity => entity.Name).HasMethod("hash");

        // The import path compares normalized names repeatedly. PostgreSQL cannot use the Name
        // hash index for lower(Name), so expose the normalized expression as a generated column
        // and have the PostgreSQL query path address it directly.
        people.Property<string>("NameLower")
            .HasComputedColumnSql("lower(\"Name\")", stored: true)
            .UseCollation("C");
        people.HasIndex("NameLower").HasMethod("hash");
    }

    private static void ConfigureMediaStreamIndexes(ModelBuilder modelBuilder)
    {
        // Language is a BCP-47/ISO language tag used as a filter discriminator, not free-form
        // metadata. 64 characters accommodates private-use tags while keeping the covering B-tree.
        modelBuilder.Entity<MediaStreamInfo>().Property(entity => entity.Language).HasMaxLength(64);
    }

    private static void ConfigureDeviceIndexes(ModelBuilder modelBuilder)
    {
        // Access tokens are generated as 32 hexadecimal characters and device ids have an existing
        // application limit of 256 characters. DeviceOptions uses the same device-id domain.
        modelBuilder.Entity<ApiKey>().Property(entity => entity.AccessToken).HasMaxLength(64);
        modelBuilder.Entity<Device>().Property(entity => entity.AccessToken).HasMaxLength(64);
        modelBuilder.Entity<DeviceOptions>().Property(entity => entity.DeviceId).HasMaxLength(256);
    }

    private static void ConfigureProviderIdIndexes(ModelBuilder modelBuilder)
    {
        var providers = modelBuilder.Entity<BaseItemProvider>();
        // Provider ids originate in plugins and are not bounded by a shared application contract.
        // Use their client-computed SHA-256 digest in the physical key so PostgreSQL can enforce
        // item/provider uniqueness without rejecting otherwise valid long identifiers.
        providers.HasKey(entity => new { entity.ItemId, entity.ProviderIdDigest });
        providers.Property(entity => entity.ProviderId).IsRequired();
        providers.Property(entity => entity.ProviderIdDigest).ValueGeneratedNever();
        RemoveIndex(providers.Metadata, nameof(BaseItemProvider.ProviderId), nameof(BaseItemProvider.ItemId), nameof(BaseItemProvider.ProviderValue));
        providers.HasIndex(entity => entity.ProviderId).HasMethod("hash");
        providers.HasIndex(entity => entity.ProviderValue).HasMethod("hash");
    }

    private static void ConfigureItemValueIndexes(ModelBuilder modelBuilder)
    {
        var itemValues = modelBuilder.Entity<ItemValue>();
        RemoveIndex(itemValues.Metadata, nameof(ItemValue.Type), nameof(ItemValue.CleanValue));
        RemoveIndex(itemValues.Metadata, nameof(ItemValue.Type), nameof(ItemValue.Value));
        itemValues.HasIndex(entity => entity.Type);
        itemValues.HasIndex(entity => entity.CleanValue).HasMethod("hash");

        // PostgreSQL cannot enforce uniqueness directly with a hash index and a B-tree cannot store
        // arbitrary-length metadata. The entity setter computes SHA-256 over the UTF-8 text so the
        // unique index has a fixed-size key. PostgreSQL marks convert_to(text, name) STABLE rather
        // than IMMUTABLE, so the equivalent server-side generated expression is invalid DDL.
        itemValues.Property(entity => entity.ValueDigest).ValueGeneratedNever();
        itemValues.HasIndex(entity => new { entity.Type, entity.ValueDigest }).IsUnique();
    }

    private static void ConfigureUnboundedCompositeKeys(ModelBuilder modelBuilder)
    {
        // Roles and custom data keys are client/plugin-supplied text without a shared length limit.
        // PostgreSQL B-trees cannot safely use arbitrary-length text as primary-key columns, so use
        // application-computed SHA-256 digests while retaining the original text for exact queries.
        var peopleMap = modelBuilder.Entity<PeopleBaseItemMap>();
        peopleMap.HasKey(entity => new { entity.ItemId, entity.PeopleId, entity.RoleDigest });
        peopleMap.Property(entity => entity.Role).IsRequired();
        peopleMap.Property(entity => entity.RoleDigest).ValueGeneratedNever();

        var userData = modelBuilder.Entity<UserData>();
        userData.HasKey(entity => new { entity.ItemId, entity.UserId, entity.CustomDataKeyDigest });
        userData.Property(entity => entity.CustomDataKey).IsRequired();
        userData.Property(entity => entity.CustomDataKeyDigest).ValueGeneratedNever();
    }

    private static void ConfigureCustomPreferenceIndexes(ModelBuilder modelBuilder)
    {
        var preferences = modelBuilder.Entity<CustomItemDisplayPreferences>();
        RemoveIndex(
            preferences.Metadata,
            nameof(CustomItemDisplayPreferences.UserId),
            nameof(CustomItemDisplayPreferences.ItemId),
            nameof(CustomItemDisplayPreferences.Client),
            nameof(CustomItemDisplayPreferences.Key));

        // Preference keys are supplied by clients and are not a bounded identifier. Preserve their
        // uniqueness without changing SQLite validation or truncating valid PostgreSQL writes.
        preferences.Property(entity => entity.KeyDigest).ValueGeneratedNever();
        preferences.HasIndex(
                entity => new { entity.UserId, entity.ItemId, entity.Client, entity.KeyDigest })
            .IsUnique();
    }

    private static void RemoveIndex(IMutableEntityType entityType, params string[] propertyNames)
    {
        var properties = propertyNames.Select(
            propertyName => entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"Property '{entityType.DisplayName()}.{propertyName}' was not configured."));
        var index = entityType.FindIndex(properties.ToArray());
        if (index is null)
        {
            throw new InvalidOperationException(
                $"Expected index '{entityType.DisplayName()}({string.Join(", ", propertyNames)})' was not configured.");
        }

        entityType.RemoveIndex(index);
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
