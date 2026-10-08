using System;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Migrations.Routines;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class StripEmbeddedLinkedChildrenTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    public StripEmbeddedLinkedChildrenTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public void Perform_RemovesOnlyDeprecatedTopLevelPropertiesAndPreservesMalformedData()
    {
        var migratedId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var malformedId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var untouchedId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        const string MalformedData = "{\"LinkedChildren\":";
        const string UntouchedData = "{\"Name\":\"kept\",\"Nested\":{\"LinkedChildren\":[1]}}";

        using (var context = CreateDbContext())
        {
            context.BaseItems.AddRange(
                new BaseItemEntity
                {
                    Id = migratedId,
                    Type = "test",
                    Data = "{\"Name\":\"kept\",\"LinkedChildren\":[1],\"ExtraIds\":{},\"SupportsExternalTransfer\":true}"
                },
                new BaseItemEntity { Id = malformedId, Type = "test", Data = MalformedData },
                new BaseItemEntity { Id = untouchedId, Type = "test", Data = UntouchedData });
            context.SaveChanges();
        }

        CreateMigration().Perform();

        using var verification = CreateDbContext();
        var migrated = JsonNode.Parse(verification.BaseItems.Find(migratedId)!.Data!)!.AsObject();
        Assert.Equal("kept", migrated["Name"]!.GetValue<string>());
        Assert.False(migrated.ContainsKey("LinkedChildren"));
        Assert.False(migrated.ContainsKey("ExtraIds"));
        Assert.False(migrated.ContainsKey("SupportsExternalTransfer"));
        Assert.Equal(MalformedData, verification.BaseItems.Find(malformedId)!.Data);
        Assert.Equal(UntouchedData, verification.BaseItems.Find(untouchedId)!.Data);
    }

    private JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(new Mock<IApplicationPaths>().Object, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private StripEmbeddedLinkedChildren CreateMigration()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        return new StripEmbeddedLinkedChildren(NullLoggerFactory.Instance, factory.Object);
    }
}
