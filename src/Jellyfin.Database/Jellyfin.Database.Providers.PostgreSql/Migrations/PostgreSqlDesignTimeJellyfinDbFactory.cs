using System;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.DbConfiguration;
using Jellyfin.Database.Implementations.Locking;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Database.Providers.PostgreSql.Migrations;

/// <summary>
/// The design-time factory for PostgreSQL migrations.
/// </summary>
internal sealed class PostgreSqlDesignTimeJellyfinDbFactory : IDesignTimeDbContextFactory<JellyfinDbContext>
{
    private const string DesignTimeConnectionString = "Host=localhost;Database=jellyfin;Username=jellyfin";

    /// <inheritdoc/>
    public JellyfinDbContext CreateDbContext(string[] args)
    {
        ValidateMigrationProviderArgument(args);

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
                    ConnectionString = DesignTimeConnectionString
                }
            });

        return new JellyfinDbContext(
            optionsBuilder.Options,
            NullLogger<JellyfinDbContext>.Instance,
            provider,
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private static void ValidateMigrationProviderArgument(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals("--migration-provider", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Length
                || !args[index + 1].Equals(DatabaseProviderKey.PostgreSql, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The PostgreSQL design-time factory requires '--migration-provider {DatabaseProviderKey.PostgreSql}'.");
            }

            return;
        }
    }
}
