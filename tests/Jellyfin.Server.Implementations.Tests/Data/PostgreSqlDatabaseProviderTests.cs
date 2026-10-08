using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.PostgreSql;
using Jellyfin.Database.Providers.PostgreSql.Migrations;
using Jellyfin.Database.Providers.PostgreSql.ValueConverters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data;

public class PostgreSqlDatabaseProviderTests
{
    private const string ConnectionString = "Host=database.internal;Port=5544;Database=jellyfin;Username=jellyfin;Password=secret";

    [Fact]
    public void Capabilities_DoNotAdvertiseUnrestorableBackupPaths()
    {
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());

        Assert.Equal(DatabaseProviderCapabilities.None, provider.Capabilities);
    }

    [Fact]
    public async Task FastMigrationBackupOperations_AreRejectedWithActionableGuidance()
    {
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());

        var createException = await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.MigrationBackupFast(TestContext.Current.CancellationToken));
        var restoreResult = await provider.TryRestoreBackupFast("unused", TestContext.Current.CancellationToken);
        var deleteResult = await provider.TryDeleteBackup("unused");

        Assert.Contains("external server-side backup tooling", createException.Message, StringComparison.Ordinal);
        Assert.Contains("before upgrading", createException.Message, StringComparison.Ordinal);
        Assert.False(restoreResult.Succeeded);
        Assert.Contains("before upgrading", restoreResult.ErrorMessage, StringComparison.Ordinal);
        Assert.False(deleteResult.Succeeded);
        Assert.Contains("before upgrading", deleteResult.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", createException.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Maintenance_WithCancelledToken_DoesNotCreateContext()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>())
        {
            DbContextFactory = factory.Object
        };
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.RefreshStatistics(cancellationTokenSource.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.RunScheduledOptimisation(cancellationTokenSource.Token));

        factory.Verify(
            item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ShutdownTask_IsBoundedAndHonorsCancellation()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>())
        {
            DbContextFactory = factory.Object
        };

        await provider.RunShutdownTask(CancellationToken.None);
        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.RunShutdownTask(cancellationTokenSource.Token));

        factory.Verify(
            item => item.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Initialise_ConfiguresNpgsqlMigrationsAssemblyAndDefaultCommandTimeout()
    {
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());

        provider.Initialise(optionsBuilder, CreateConfiguration(ConnectionString));

        var extension = optionsBuilder.Options.Extensions.OfType<RelationalOptionsExtension>().Single();
        Assert.Equal(typeof(PostgreSqlDatabaseProvider).Assembly.GetName().Name, extension.MigrationsAssembly);
        Assert.Equal(PostgreSqlDatabaseProviderOptions.DefaultCommandTimeoutSeconds, extension.CommandTimeout);
    }

    [Fact]
    public void Initialise_UsesCaseInsensitiveCustomCommandTimeout()
    {
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());
        var configuration = CreateConfiguration(ConnectionString);
        configuration.CustomProviderOptions!.Options = new Collection<CustomDatabaseOption>
        {
            new()
            {
                Key = "COMMAND-TIMEOUT",
                Value = "45"
            }
        };

        provider.Initialise(optionsBuilder, configuration);

        var extension = optionsBuilder.Options.Extensions.OfType<RelationalOptionsExtension>().Single();
        Assert.Equal(45, extension.CommandTimeout);
    }

    [Fact]
    public void ExternalMigrationBackupAcknowledgement_MustMatchNewestPendingMigration()
    {
        const string TargetMigration = "20261008114025_AddActivityLogTypeDateIndex";
        var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>();
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());
        var configuration = CreateConfiguration(ConnectionString);
        configuration.CustomProviderOptions!.Options = new Collection<CustomDatabaseOption>
        {
            new()
            {
                Key = "MIGRATION-BACKUP-ACKNOWLEDGEMENT",
                Value = TargetMigration
            }
        };
        provider.Initialise(optionsBuilder, configuration);

        var accepted = provider.ValidateExternalMigrationBackupAcknowledgement(
            ["20261008095117_InitialPostgreSqlBaseline", TargetMigration]);
        var rejected = provider.ValidateExternalMigrationBackupAcknowledgement(
            [TargetMigration, "20261101000000_LaterMigration"]);

        Assert.True(accepted.Succeeded);
        Assert.False(rejected.Succeeded);
        Assert.Contains(PostgreSqlDatabaseProviderOptions.MigrationBackupAcknowledgement, rejected.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("20261101000000_LaterMigration", rejected.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AppliedMigrationHistory_MustBeAKnownOrderedProviderPrefix()
    {
        const string Baseline = "20261008095117_InitialPostgreSqlBaseline";
        const string Target = "20261008114025_AddActivityLogTypeDateIndex";
        const string CodeMigration = "20250420000000_CreateNetworkConfiguration";
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());

        var supported = provider.ValidateAppliedMigrationHistory(
            [CodeMigration, Baseline],
            [Baseline, Target],
            [CodeMigration]);
        var future = provider.ValidateAppliedMigrationHistory(
            [CodeMigration, Baseline, Target, "20990101000000_FuturePostgreSqlSchema"],
            [Baseline, Target],
            [CodeMigration]);
        var gap = provider.ValidateAppliedMigrationHistory(
            [CodeMigration, Target],
            [Baseline, Target],
            [CodeMigration]);

        Assert.True(supported.Succeeded);
        Assert.False(future.Succeeded);
        Assert.Contains("not supported", future.ErrorMessage, StringComparison.Ordinal);
        Assert.False(gap.Succeeded);
        Assert.Contains("ordered prefix", gap.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialise_MalformedConnectionString_DoesNotExposePassword()
    {
        const string Password = "correct horse battery staple";
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.Initialise(
                new DbContextOptionsBuilder<JellyfinDbContext>(),
                CreateConfiguration($"Host=db;Database=jellyfin;Password={Password};NotAKeyword=value")));

        Assert.Contains("invalid PostgreSQL connection string", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Database=jellyfin", "Host")]
    [InlineData("Host=database.internal", "Database")]
    public void Initialise_MissingRequiredConnectionValue_ThrowsActionableError(
        string connectionString,
        string missingValue)
    {
        var provider = new PostgreSqlDatabaseProvider(Mock.Of<ILogger<PostgreSqlDatabaseProvider>>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.Initialise(
                new DbContextOptionsBuilder<JellyfinDbContext>(),
                CreateConfiguration(connectionString)));

        Assert.Contains(missingValue, exception.Message, StringComparison.Ordinal);
        Assert.Contains("CustomProviderOptions.ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Initialise_LogDoesNotExposePasswordOrConnectionString()
    {
        var logger = new Mock<ILogger<PostgreSqlDatabaseProvider>>();
        var provider = new PostgreSqlDatabaseProvider(logger.Object);

        provider.Initialise(new DbContextOptionsBuilder<JellyfinDbContext>(), CreateConfiguration(ConnectionString));

        Assert.DoesNotContain(
            logger.Invocations.SelectMany(invocation => invocation.Arguments),
            argument => argument?.ToString()?.Contains("secret", StringComparison.Ordinal) is true);
        Assert.DoesNotContain(
            logger.Invocations.SelectMany(invocation => invocation.Arguments),
            argument => argument?.ToString()?.Contains(ConnectionString, StringComparison.Ordinal) is true);
    }

    [Fact]
    public void DesignTimeFactory_WithMigrationProviderArgument_CreatesNpgsqlContext()
    {
        var factory = new PostgreSqlDesignTimeJellyfinDbFactory();

        using var context = factory.CreateDbContext(["--migration-provider", "jellyfin-pgsql"]);

        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", context.Database.ProviderName);
        var extension = context.GetService<IDbContextOptions>().Extensions.OfType<RelationalOptionsExtension>().Single();
        Assert.Equal(typeof(PostgreSqlDatabaseProvider).Assembly.GetName().Name, extension.MigrationsAssembly);
    }

    [Fact]
    public void UtcDateTimeValueConverter_NormalizesUnspecifiedAndLocalValues()
    {
        var converter = new UtcDateTimeValueConverter();
        var unspecified = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Unspecified);
        var local = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Local);

        Assert.Equal(
            DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
            Assert.IsType<DateTime>(converter.ConvertToProvider(unspecified)));
        Assert.Equal(
            local.ToUniversalTime(),
            Assert.IsType<DateTime>(converter.ConvertToProvider(local)));
    }

    [Fact]
    public void UtcDateTimeOffsetValueConverter_NormalizesOffset()
    {
        var converter = new UtcDateTimeOffsetValueConverter();
        var withOffset = new DateTimeOffset(2026, 4, 5, 6, 7, 8, TimeSpan.FromHours(5));

        var normalized = Assert.IsType<DateTimeOffset>(converter.ConvertToProvider(withOffset));

        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(withOffset.UtcDateTime, normalized.UtcDateTime);
    }

    private static DatabaseConfigurationOptions CreateConfiguration(string connectionString)
    {
        return new DatabaseConfigurationOptions
        {
            DatabaseType = DatabaseProviderKey.PostgreSql,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = connectionString
            }
        };
    }
}
