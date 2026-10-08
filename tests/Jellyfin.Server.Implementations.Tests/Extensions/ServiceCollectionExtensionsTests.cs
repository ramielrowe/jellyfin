using System;
using System.Collections.ObjectModel;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.PostgreSql;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Extensions;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddJellyfinDbContext_PostgreSqlWithoutConnectionString_ThrowsActionableError()
    {
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = DatabaseProviderKey.PostgreSql
        };

        using var services = CreateServices(configuration).BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>());

        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.Contains("CustomProviderOptions.ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJellyfinDbContext_PostgreSqlMalformedConnectionString_DoesNotExposeConnectionString()
    {
        const string Secret = "Host=db;Database=jellyfin;Username=jellyfin;Password=correct horse battery staple;NotAKeyword=value";
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = DatabaseProviderKey.PostgreSql,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = Secret
            }
        };

        using var services = CreateServices(configuration).BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>());

        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery staple", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void AddJellyfinDbContext_PostgreSqlWithInvalidCommandTimeout_ThrowsCredentialSafeError(string commandTimeout)
    {
        const string Secret = "Host=db;Database=jellyfin;Username=jellyfin;Password=correct horse battery staple";
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = DatabaseProviderKey.PostgreSql,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = Secret,
                Options = new Collection<CustomDatabaseOption>
                {
                    new()
                    {
                        Key = PostgreSqlDatabaseProviderOptions.CommandTimeout,
                        Value = commandTimeout
                    }
                }
            }
        };

        using var services = CreateServices(configuration).BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(
            () => services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>());

        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.Contains(PostgreSqlDatabaseProviderOptions.CommandTimeout, exception.Message, StringComparison.Ordinal);
        Assert.Contains("positive whole number", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery staple", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJellyfinDbContext_PostgreSqlWithPositiveCommandTimeout_ResolvesProvider()
    {
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = DatabaseProviderKey.PostgreSql,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = "Host=db;Database=jellyfin",
                Options = new Collection<CustomDatabaseOption>
                {
                    new()
                    {
                        Key = "COMMAND-TIMEOUT",
                        Value = "1"
                    }
                }
            }
        };

        using var services = CreateServices(configuration).BuildServiceProvider();

        Assert.IsType<PostgreSqlDatabaseProvider>(services.GetRequiredService<IJellyfinDatabaseProvider>());
        Assert.NotNull(services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>());
    }

    [Theory]
    [InlineData("Jellyfin-PgSql")]
    [InlineData("jellyfin-pgsql")]
    public void AddJellyfinDbContext_PostgreSqlLookupIsCaseInsensitive(string providerKey)
    {
        var services = CreateServices(new DatabaseConfigurationOptions
        {
            DatabaseType = providerKey,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = "Host=db;Database=jellyfin"
            }
        });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<PostgreSqlDatabaseProvider>(provider.GetRequiredService<IJellyfinDatabaseProvider>());
    }

    [Theory]
    [InlineData(DatabaseLockingBehaviorTypes.Pessimistic)]
    [InlineData(DatabaseLockingBehaviorTypes.Optimistic)]
    public void AddJellyfinDbContext_PostgreSqlRejectsSqliteLockingBehaviors(DatabaseLockingBehaviorTypes lockingBehavior)
    {
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = DatabaseProviderKey.PostgreSql,
            LockingBehavior = lockingBehavior,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = "Host=db;Database=jellyfin"
            }
        };

        var exception = Assert.Throws<InvalidOperationException>(() => CreateServices(configuration));

        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DatabaseLockingBehaviorTypes.NoLock), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJellyfinDbContext_PostgreSqlDefaultsToNoApplicationLock()
    {
        var configuration = new DatabaseConfigurationOptions
        {
            DatabaseType = DatabaseProviderKey.PostgreSql,
            CustomProviderOptions = new CustomDatabaseOptions
            {
                PluginName = string.Empty,
                PluginAssembly = string.Empty,
                ConnectionString = "Host=db;Database=jellyfin"
            }
        };

        using var provider = CreateServices(configuration).BuildServiceProvider();

        Assert.IsType<NoLockBehavior>(provider.GetRequiredService<IEntityFrameworkCoreLockingBehavior>());
    }

    [Fact]
    public void AddJellyfinDbContext_UnknownProvider_ListsBothBuiltInProviders()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => CreateServices(new DatabaseConfigurationOptions { DatabaseType = "unknown" }));

        Assert.Contains(DatabaseProviderKey.Sqlite, exception.Message, StringComparison.Ordinal);
        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Jellyfin-SQLite")]
    [InlineData("jellyfin-sqlite")]
    public void AddJellyfinDbContext_SqliteLookupIsCaseInsensitive(string providerKey)
    {
        var services = CreateServices(new DatabaseConfigurationOptions { DatabaseType = providerKey });
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqliteDatabaseProvider>(provider.GetRequiredService<IJellyfinDatabaseProvider>());
    }

    [Fact]
    public void AddJellyfinDbContext_WithoutConfiguration_DefaultsToSqlite()
    {
        var configurationManager = new Mock<IServerConfigurationManager>();

        var applicationPaths = new Mock<IApplicationPaths>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(applicationPaths.Object);
        services.AddJellyfinDbContext(configurationManager.Object, new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.IsType<SqliteDatabaseProvider>(provider.GetRequiredService<IJellyfinDatabaseProvider>());
        configurationManager.Verify(
            manager => manager.SaveConfiguration(
                "database",
                It.Is<DatabaseConfigurationOptions>(options => options.DatabaseType == DatabaseProviderKey.Sqlite)),
            Times.Once);
    }

    private static ServiceCollection CreateServices(DatabaseConfigurationOptions databaseConfiguration)
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager
            .Setup(c => c.GetConfiguration("database"))
            .Returns(databaseConfiguration);

        var applicationPaths = new Mock<IApplicationPaths>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(applicationPaths.Object);
        services.AddJellyfinDbContext(configurationManager.Object, new ConfigurationBuilder().Build());
        return services;
    }
}
