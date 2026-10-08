using System;
using System.Collections.ObjectModel;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
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

        var exception = Assert.Throws<InvalidOperationException>(() => CreateServices(configuration));

        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.Contains("CustomProviderOptions.ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJellyfinDbContext_PostgreSqlLookupError_DoesNotExposeConnectionString()
    {
        const string Secret = "Host=db;Database=jellyfin;Username=jellyfin;Password=correct horse battery staple";
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

        var exception = Assert.Throws<InvalidOperationException>(() => CreateServices(configuration));

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

        var exception = Assert.Throws<InvalidOperationException>(() => CreateServices(configuration));

        Assert.Contains(DatabaseProviderKey.PostgreSql, exception.Message, StringComparison.Ordinal);
        Assert.Contains(PostgreSqlDatabaseProviderOptions.CommandTimeout, exception.Message, StringComparison.Ordinal);
        Assert.Contains("positive whole number", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("correct horse battery staple", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddJellyfinDbContext_PostgreSqlWithPositiveCommandTimeout_ContinuesToProviderLookup()
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

        var exception = Assert.Throws<InvalidOperationException>(() => CreateServices(configuration));

        Assert.Contains("cannot find the database provider", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("positive whole number", exception.Message, StringComparison.Ordinal);
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
