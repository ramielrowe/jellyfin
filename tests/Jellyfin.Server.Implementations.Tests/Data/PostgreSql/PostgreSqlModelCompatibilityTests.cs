using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Emby.Server.Implementations.Library.Search;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Activity;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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

        Assert.All(
            context.Model.GetEntityTypes().SelectMany(entity => entity.GetIndexes()),
            index =>
            {
                var indexedStrings = index.Properties.Where(property => property.ClrType == typeof(string)).ToArray();
                if (indexedStrings.Length > 0 && index.GetMethod() != "hash")
                {
                    Assert.All(indexedStrings, property => Assert.NotNull(property.GetMaxLength()));
                    Assert.InRange(indexedStrings.Sum(property => property.GetMaxLength()!.Value), 1, 512);
                }
            });
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
            var grandchildId = Guid.NewGuid();
            var otherId = Guid.NewGuid();
            var parent = CreateItem(parentId, "Folder", true);
            var child = CreateItem(childId, "Movie", false);
            var grandchild = CreateItem(grandchildId, "Episode", false);
            var other = CreateItem(otherId, "Movie", false);
            context.BaseItems.AddRange(parent, child, grandchild, other);
            context.AncestorIds.AddRange(
                new AncestorId
                {
                    ItemId = childId,
                    ParentItemId = parentId,
                    Item = child,
                    ParentItem = parent
                },
                new AncestorId
                {
                    ItemId = grandchildId,
                    ParentItemId = parentId,
                    Item = grandchild,
                    ParentItem = parent
                },
                new AncestorId
                {
                    ItemId = grandchildId,
                    ParentItemId = childId,
                    Item = grandchild,
                    ParentItem = child
                });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            context.ChangeTracker.Clear();

            var selected = await context.BaseItems
                .WhereOneOrMany(new[] { parentId, childId }, entity => entity.Id)
                .Select(entity => entity.Id)
                .ToHashSetAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(new[] { parentId, childId }.Order(), selected.Order());

            var singleton = await context.BaseItems
                .WhereOneOrMany(new[] { otherId }, entity => entity.Id)
                .Select(entity => entity.Id)
                .SingleAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(otherId, singleton);

            Assert.Empty(await context.BaseItems
                .WhereOneOrMany(Array.Empty<Guid>(), entity => entity.Id)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true));

            var descendants = await DescendantQueryHelper.GetAllDescendantIds(context, parentId)
                .ToListAsync(TestContext.Current.CancellationToken)
                .ConfigureAwait(true);
            Assert.Equal(new[] { childId, grandchildId }.Order(), descendants.Order());

            var partitioned = new List<Guid>();
            await foreach (var item in context.BaseItems
                .Where(entity => entity.Type != "PLACEHOLDER")
                .OrderBy(entity => entity.Id)
                .PartitionEagerAsync(2, cancellationToken: TestContext.Current.CancellationToken))
            {
                partitioned.Add(item.Id);
            }

            Assert.Equal(new[] { parentId, childId, grandchildId, otherId }.Order(), partitioned);
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

    [Fact]
    public async Task LongIndexedTextValues_CanBeSavedAndFoundOnPostgreSql()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            var id = Guid.NewGuid();
            var longValue = "/media/" + new string('x', 12_000);
            context.BaseItems.Add(new BaseItemEntity
            {
                Id = id,
                Type = "Movie",
                Path = longValue,
                Name = longValue,
                CleanName = longValue,
                SortName = longValue,
                SeriesName = longValue,
                PresentationUniqueKey = longValue,
                IsFolder = false
            });

            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            context.ChangeTracker.Clear();

            Assert.Equal(
                id,
                await context.BaseItems
                    .Where(item => item.Path == longValue)
                    .Select(item => item.Id)
                    .SingleAsync(TestContext.Current.CancellationToken)
                    .ConfigureAwait(true));
        }
    }

    [Fact]
    public async Task SearchPaths_AreCaseInsensitiveAndTreatWildcardsLiterallyOnPostgreSql()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        var database = await _fixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var itemTypeLookup = new ItemTypeLookup();
        var movieType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];
        var originalTitleId = Guid.NewGuid();
        var sortNameId = Guid.NewGuid();
        var literalId = Guid.NewGuid();
        await using (context.ConfigureAwait(true))
        {
            context.BaseItems.AddRange(
                CreateSearchItem(originalTitleId, movieType, "Unrelated", "unrelated", "Unrelated", "HIDDEN TITLE"),
                CreateSearchItem(sortNameId, movieType, "Other", "other", "SORT SHAPE", null),
                CreateSearchItem(literalId, movieType, "Literal", "literal", "Literal", @"Rate C:\100%_DONE"),
                CreateSearchItem(Guid.NewGuid(), movieType, "Decoy", "decoy", "Decoy", "Rate 100-percent done"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var repository = CreateRepository(database, itemTypeLookup);
        Assert.Equal(originalTitleId, Assert.Single(repository.GetItemList(Query(searchTerm: "hidden title"))).Id);
        Assert.Equal(originalTitleId, Assert.Single(repository.GetItemList(Query(nameContains: "HIDDEN TITLE"))).Id);
        Assert.Equal(literalId, Assert.Single(repository.GetItemList(Query(searchTerm: @"C:\100%_done"))).Id);
        Assert.Equal(literalId, Assert.Single(repository.GetItemList(Query(nameContains: @"C:\100%_DONE"))).Id);

        var searchProvider = CreateSearchProvider(database, itemTypeLookup, repository);
        var originalResults = await searchProvider.SearchAsync(
            new SearchProviderQuery { SearchTerm = "hidden title" },
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var sortResults = await searchProvider.SearchAsync(
            new SearchProviderQuery { SearchTerm = "sort shape" },
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        var literalResults = await searchProvider.SearchAsync(
            new SearchProviderQuery { SearchTerm = @"C:\100%_done" },
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(100f, Assert.Single(originalResults, result => result.ItemId.Equals(originalTitleId)).Score);
        Assert.Equal(100f, Assert.Single(sortResults, result => result.ItemId.Equals(sortNameId)).Score);
        Assert.Equal(literalId, Assert.Single(literalResults).ItemId);
    }

    [Theory]
    [InlineData(ItemSortBy.SortName)]
    [InlineData(ItemSortBy.DateCreated)]
    [InlineData(ItemSortBy.CommunityRating)]
    public async Task NullableSortKeys_PlaceNullLastOnPostgreSql(ItemSortBy sortBy)
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        var database = await _fixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var itemTypeLookup = new ItemTypeLookup();
        var movieType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var nullId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        await using (context.ConfigureAwait(true))
        {
            context.BaseItems.AddRange(
                CreateSearchItem(firstId, movieType, "First", "first", "Alpha", null, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1),
                CreateSearchItem(secondId, movieType, "Second", "second", "Zulu", null, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 2),
                CreateSearchItem(nullId, movieType, "Null", "null", null, null));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var repository = CreateRepository(database, itemTypeLookup);
        var ascending = Query();
        ascending.OrderBy = [(sortBy, SortOrder.Ascending)];
        var descending = Query();
        descending.OrderBy = [(sortBy, SortOrder.Descending)];

        Assert.Equal([firstId, secondId, nullId], repository.GetItemList(ascending).Select(item => item.Id));
        Assert.Equal([secondId, firstId, nullId], repository.GetItemList(descending).Select(item => item.Id));
    }

    [Fact]
    public async Task ActivityTextFilters_TreatLikeMetacharactersLiterallyOnPostgreSql()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            context.ActivityLogs.AddRange(
                new ActivityLog(@"Progress C:\100%_done", "Literal", Guid.Empty),
                new ActivityLog("Progress 100-percent done", "Literal", Guid.Empty));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var database = await _fixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var manager = new ActivityManager(CreateDbContextFactory(database));
        var result = await manager.GetPagedResultAsync(new ActivityLogQuery { Name = @"C:\100%_DONE" }).ConfigureAwait(true);

        Assert.Equal(@"Progress C:\100%_done", Assert.Single(result.Items).Name);
    }

    [Fact]
    public async Task PeopleRanges_UseCaseInsensitiveOrdinalOrderingOnPostgreSql()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            foreach (var name in new[] { "alpha", "Brad", "bob", "Zoe", "éclair" })
            {
                context.Peoples.Add(new People { Id = Guid.NewGuid(), Name = name, PersonType = "Actor" });
            }

            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var database = await _fixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var repository = new PeopleRepository(
            CreateDbContextFactory(database),
            new ItemTypeLookup(),
            new Mock<IItemQueryHelpers>().Object);
        var asciiRange = repository.GetPeople(new InternalPeopleQuery
        {
            NameStartsWithOrGreater = "B",
            NameLessThan = "C"
        });
        var nonAsciiPrefix = repository.GetPeople(new InternalPeopleQuery { NameStartsWith = "é" });

        Assert.Equal(new[] { "bob", "Brad" }, asciiRange.Items.Select(person => person.Name));
        Assert.Equal("éclair", Assert.Single(nonAsciiPrefix.Items).Name);
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

    private static BaseItemEntity CreateSearchItem(
        Guid id,
        string type,
        string name,
        string cleanName,
        string? sortName,
        string? originalTitle,
        DateTime? dateCreated = null,
        float? communityRating = null)
        => new()
        {
            Id = id,
            Type = type,
            Name = name,
            CleanName = cleanName,
            SortName = sortName,
            OriginalTitle = originalTitle,
            DateCreated = dateCreated,
            CommunityRating = communityRating,
            MediaType = "Video",
            IsMovie = true,
            IsFolder = false,
            IsVirtualItem = false,
            PresentationUniqueKey = id.ToString("N")
        };

    private static InternalItemsQuery Query(string? searchTerm = null, string? nameContains = null)
        => new()
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            IncludeOwnedItems = true,
            SearchTerm = searchTerm,
            NameContains = nameContains
        };

    private static IDbContextFactory<JellyfinDbContext> CreateDbContextFactory(PostgreSqlTestDatabase database)
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(instance => instance.CreateDbContext()).Returns(database.CreateDbContext);
        factory.Setup(instance => instance.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(database.CreateDbContext);
        return factory.Object;
    }

    private static BaseItemRepository CreateRepository(PostgreSqlTestDatabase database, ItemTypeLookup itemTypeLookup)
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(manager => manager.Configuration).Returns(new ServerConfiguration());
        return new BaseItemRepository(
            CreateDbContextFactory(database),
            new Mock<IServerApplicationHost>().Object,
            itemTypeLookup,
            configurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
    }

    private static SqlSearchProvider CreateSearchProvider(
        PostgreSqlTestDatabase database,
        ItemTypeLookup itemTypeLookup,
        BaseItemRepository repository)
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(manager => manager.Configuration).Returns(new ServerConfiguration());
        return new SqlSearchProvider(
            CreateDbContextFactory(database),
            itemTypeLookup,
            new Mock<ILibraryManager>().Object,
            new Mock<IUserManager>().Object,
            repository,
            configurationManager.Object);
    }
}
