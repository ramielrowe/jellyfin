using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
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
    public async Task PerformAsync_RemovesOnlyDeprecatedTopLevelPropertiesAndPreservesMalformedData()
    {
        var migratedId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var malformedId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var untouchedId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        const string MalformedData = "{\"LinkedChildren\":";
        const string UntouchedData = "{\"Name\":\"kept\",\"Nested\":{\"LinkedChildren\":[1]}}";

        await using (var context = CreateDbContext())
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
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await CreateMigration().PerformAsync(TestContext.Current.CancellationToken);

        await using var verification = CreateDbContext();
        var migrated = JsonNode.Parse((await verification.BaseItems.FindAsync([migratedId], TestContext.Current.CancellationToken))!.Data!)!.AsObject();
        Assert.Equal("kept", migrated["Name"]!.GetValue<string>());
        Assert.False(migrated.ContainsKey("LinkedChildren"));
        Assert.False(migrated.ContainsKey("ExtraIds"));
        Assert.False(migrated.ContainsKey("SupportsExternalTransfer"));
        Assert.Equal(MalformedData, (await verification.BaseItems.FindAsync([malformedId], TestContext.Current.CancellationToken))!.Data);
        Assert.Equal(UntouchedData, (await verification.BaseItems.FindAsync([untouchedId], TestContext.Current.CancellationToken))!.Data);
    }

    [Fact]
    public async Task PerformAsync_ProcessesMultipleBatchesWithoutSkippingBoundaryRows()
    {
        var itemCount = (StripEmbeddedLinkedChildren.BatchSize * 2) + 1;
        await using (var context = CreateDbContext())
        {
            context.BaseItems.AddRange(Enumerable.Range(1, itemCount).Select(index => new BaseItemEntity
            {
                Id = CreateOrderedGuid(index + 1),
                Type = "test",
                Data = $"{{\"Name\":\"item-{index}\",\"LinkedChildren\":[1]}}"
            }));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await CreateMigration().PerformAsync(TestContext.Current.CancellationToken);

        await using var verification = CreateDbContext();
        var items = await verification.BaseItems
            .Where(item => item.Type == "test")
            .OrderBy(item => item.Id)
            .Select(item => item.Data)
            .ToArrayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(itemCount, items.Length);
        Assert.All(items, data => Assert.DoesNotContain("LinkedChildren", data, StringComparison.Ordinal));
        Assert.Contains(items, data => data!.Contains($"item-{StripEmbeddedLinkedChildren.BatchSize}", StringComparison.Ordinal));
        Assert.Contains(items, data => data!.Contains($"item-{StripEmbeddedLinkedChildren.BatchSize + 1}", StringComparison.Ordinal));
        Assert.Contains(items, data => data!.Contains($"item-{StripEmbeddedLinkedChildren.BatchSize * 2}", StringComparison.Ordinal));
        Assert.Contains(items, data => data!.Contains($"item-{(StripEmbeddedLinkedChildren.BatchSize * 2) + 1}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PerformAsync_WithCancelledToken_DoesNotModifyData()
    {
        var id = Guid.NewGuid();
        const string Data = "{\"LinkedChildren\":[1]}";
        await using (var context = CreateDbContext())
        {
            context.BaseItems.Add(new BaseItemEntity { Id = id, Type = "test", Data = Data });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var cancellationTokenSource = new CancellationTokenSource();
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CreateMigration().PerformAsync(cancellationTokenSource.Token));

        await using var verification = CreateDbContext();
        Assert.Equal(Data, (await verification.BaseItems.FindAsync([id], TestContext.Current.CancellationToken))!.Data);
    }

    private JellyfinDbContext CreateDbContext() => new(
        _dbOptions,
        NullLogger<JellyfinDbContext>.Instance,
        new SqliteDatabaseProvider(new Mock<IApplicationPaths>().Object, NullLogger<SqliteDatabaseProvider>.Instance),
        new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    private StripEmbeddedLinkedChildren CreateMigration()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDbContext);
        return new StripEmbeddedLinkedChildren(NullLoggerFactory.Instance, factory.Object);
    }

    private static Guid CreateOrderedGuid(int value)
    {
        var bytes = new byte[16];
        bytes[12] = (byte)(value >> 24);
        bytes[13] = (byte)(value >> 16);
        bytes[14] = (byte)(value >> 8);
        bytes[15] = (byte)value;
        return new Guid(bytes);
    }
}
