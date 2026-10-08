using System;
using System.Collections.ObjectModel;
using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.PostgreSql;
using Jellyfin.Database.Providers.PostgreSql.Migrations;
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
