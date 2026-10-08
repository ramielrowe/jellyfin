using Jellyfin.Database.Providers.PostgreSql.Migrations;
using Jellyfin.Database.Providers.Sqlite.Migrations;
using Jellyfin.Server.Implementations.Migrations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.EfMigrations;

public class EfMigrationTests
{
    [Fact]
    public void CheckForUnappliedMigrations_SqLite()
    {
        var dbDesignContext = new SqliteDesignTimeJellyfinDbFactory();
        var context = dbDesignContext.CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges(), "There are unapplied changes to the EFCore model for SQLite. Please create a Migration.");
    }

    [Fact]
    public void CheckForUnappliedMigrations_PostgreSql()
    {
        var dbDesignContext = new PostgreSqlDesignTimeJellyfinDbFactory();
        using var context = dbDesignContext.CreateDbContext([]);
        Assert.False(context.Database.HasPendingModelChanges(), "There are unapplied changes to the EFCore model for PostgreSQL. Please create a Migration.");
    }
}
