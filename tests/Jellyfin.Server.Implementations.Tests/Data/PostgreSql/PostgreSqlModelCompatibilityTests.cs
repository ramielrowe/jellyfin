using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Activity;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data.PostgreSql;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlModelCompatibilityTests
{
    private readonly PostgreSqlDatabaseFixture _fixture;

    public PostgreSqlModelCompatibilityTests(PostgreSqlDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task JellyfinModel_CanCreatePostgreSqlSchema()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);

        await using var context = database.CreateDbContext();
        Assert.True(await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(true));

        var relationalModel = context.Model.GetRelationalModel();
        Assert.All(relationalModel.Tables, table => Assert.InRange(table.Name.Length, 1, 63));
        Assert.All(
            relationalModel.Tables.SelectMany(table => table.Indexes),
            index => Assert.InRange(index.Name.Length, 1, 63));

        var baseItem = Assert.IsAssignableFrom<Microsoft.EntityFrameworkCore.Metadata.IReadOnlyEntityType>(
            context.Model.FindEntityType(typeof(BaseItemEntity)));
        Assert.Contains(
            baseItem.GetIndexes(),
            index => index.GetFilter() == "\"PrimaryVersionId\" IS NOT NULL");
        Assert.Contains(
            baseItem.GetIndexes(),
            index => index.GetFilter() == "\"PrimaryVersionId\" IS NULL AND (\"OwnerId\" IS NULL OR \"ExtraType\" IS NOT NULL)");
    }

    [Fact]
    public async Task RepresentativeValues_RoundTripWithPostgreSqlSemantics()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            var itemId = Guid.NewGuid();
            var unspecifiedInstant = new DateTime(2026, 4, 5, 6, 7, 8, DateTimeKind.Unspecified);
            var blurhash = new byte[] { 0, 1, 127, 255 };
            var keyframes = new long[] { 0, 10_000, 25_000 };

            var item = new BaseItemEntity
            {
                Id = itemId,
                Type = "Movie",
                Name = "Mixed Case Film",
                DateCreated = unspecifiedInstant,
                ExtraType = BaseItemExtraType.Featurette
            };
            context.BaseItems.Add(item);
            context.BaseItemImageInfos.Add(new BaseItemImageInfo
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Item = item,
                Path = "/artwork/image.png",
                ImageType = ImageInfoImageType.Primary,
                Blurhash = blurhash
            });
            context.KeyframeData.Add(new KeyframeData
            {
                ItemId = itemId,
                Item = item,
                TotalDuration = 30_000,
                KeyframeTicks = keyframes
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            context.ChangeTracker.Clear();

            var storedItem = await context.BaseItems
                .SingleAsync(entity => entity.Id.Equals(itemId), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var storedImage = await context.BaseItemImageInfos
                .SingleAsync(entity => entity.ItemId.Equals(itemId), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            var storedKeyframes = await context.KeyframeData
                .SingleAsync(entity => entity.ItemId.Equals(itemId), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal(itemId, storedItem.Id);
            Assert.Equal(BaseItemExtraType.Featurette, storedItem.ExtraType);
            Assert.Equal(DateTimeKind.Utc, storedItem.DateCreated!.Value.Kind);
            Assert.Equal(DateTime.SpecifyKind(unspecifiedInstant, DateTimeKind.Utc), storedItem.DateCreated);
            Assert.Equal(blurhash, storedImage.Blurhash);
            Assert.Equal(keyframes, storedKeyframes.KeyframeTicks);
        }
    }

    [Fact]
    public async Task UniqueItemValueConstraint_IsEnforced()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            context.ItemValues.AddRange(
                CreateItemValue("Drama"),
                CreateItemValue("Drama"));

            await Assert.ThrowsAsync<DbUpdateException>(
                () => context.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task SharedCollectionDescendantAndPartitionQueries_ExecuteOnPostgreSql()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            var parentId = Guid.NewGuid();
            var childId = Guid.NewGuid();
            var otherId = Guid.NewGuid();
            var parent = CreateItem(parentId, "Folder", true);
            var child = CreateItem(childId, "Movie", false);
            var other = CreateItem(otherId, "Movie", false);
            context.BaseItems.AddRange(parent, child, other);
            context.AncestorIds.Add(new AncestorId
            {
                ItemId = childId,
                ParentItemId = parentId,
                Item = child,
                ParentItem = parent
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            context.ChangeTracker.Clear();

            var selected = await context.BaseItems
                .WhereOneOrMany(new[] { parentId, childId }, entity => entity.Id)
                .Select(entity => entity.Id)
                .ToHashSetAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(new[] { parentId, childId }.Order(), selected.Order());

            var descendants = await DescendantQueryHelper.GetAllDescendantIds(context, parentId)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal([childId], descendants);

            var partitioned = new List<Guid>();
            await foreach (var item in context.BaseItems
                .Where(entity => entity.Type != "PLACEHOLDER")
                .OrderBy(entity => entity.Id)
                .PartitionEagerAsync(2, cancellationToken: TestContext.Current.CancellationToken))
            {
                partitioned.Add(item.Id);
            }

            Assert.Equal(new[] { parentId, childId, otherId }.Order(), partitioned);
        }
    }

    [Fact]
    public async Task ActivityTextFilters_AreCaseInsensitiveOnPostgreSql()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            context.ActivityLogs.Add(new ActivityLog("Library Scan Finished", "ScheduledTask", Guid.Empty)
            {
                Overview = "New MEDIA was discovered",
                ShortOverview = "Scan COMPLETE"
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var database = await _fixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(instance => instance.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(database.CreateDbContext);
        var manager = new ActivityManager(factory.Object);

        var result = await manager.GetPagedResultAsync(new ActivityLogQuery
        {
            Name = "scan finished",
            Overview = "media",
            ShortOverview = "complete",
            Type = "scheduledtask"
        }).ConfigureAwait(true);

        Assert.Single(result.Items);
    }

    private async Task<JellyfinDbContext> CreateSchemaAsync()
    {
        Assert.SkipUnless(_fixture.IsConfigured, _fixture.SkipReason);
        var cancellationToken = TestContext.Current.CancellationToken;
        var database = await _fixture.GetDatabaseAsync(cancellationToken).ConfigureAwait(true);
        await database.ResetAsync(cancellationToken).ConfigureAwait(true);
        var context = database.CreateDbContext();
        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(true);
        return context;
    }

    private static ItemValue CreateItemValue(string value)
        => new()
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = value,
            CleanValue = value.ToLowerInvariant()
        };

    private static BaseItemEntity CreateItem(Guid id, string type, bool isFolder)
        => new()
        {
            Id = id,
            Type = type,
            Name = type + " " + id,
            IsFolder = isFolder
        };
}
