using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Emby.Server.Implementations.Library.Search;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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

        var model = context.GetService<IDesignTimeModel>().Model;
        var relationalModel = model.GetRelationalModel();
        Assert.All(relationalModel.Tables, table => Assert.InRange(table.Name.Length, 1, 63));
        Assert.All(
            relationalModel.Tables.SelectMany(table => table.Indexes),
            index => Assert.InRange(index.Name.Length, 1, 63));

        var baseItem = Assert.IsAssignableFrom<IReadOnlyEntityType>(
            model.FindEntityType(typeof(BaseItemEntity)));
        Assert.Contains(
            baseItem.GetIndexes(),
            index => index.GetFilter() == "\"PrimaryVersionId\" IS NOT NULL");
        Assert.Contains(
            baseItem.GetIndexes(),
            index => index.GetFilter() == "\"PrimaryVersionId\" IS NULL AND (\"OwnerId\" IS NULL OR \"ExtraType\" IS NOT NULL)");

        Assert.All(
            model.GetEntityTypes().SelectMany(entity => entity.GetIndexes()),
            index =>
            {
                var indexedStrings = index.Properties.Where(property => property.ClrType == typeof(string)).ToArray();
                if (indexedStrings.Length > 0 && index.GetMethod() != "hash")
                {
                    Assert.All(indexedStrings, property => Assert.NotNull(property.GetMaxLength()));
                    Assert.InRange(indexedStrings.Sum(property => property.GetMaxLength()!.Value), 1, 512);
                }
            });

        // Base-item query families: retain every safe composite and pair each deliberately split
        // unbounded equality key with a hash index.
        AssertIndex(baseItem, null, nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.Id));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.StartDate));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.Id));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.Type), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.DateCreated));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsFolder), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.DateCreated));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.MediaType), nameof(BaseItemEntity.IsVirtualItem), nameof(BaseItemEntity.DateCreated));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.TopParentId));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.IsFolder), nameof(BaseItemEntity.IsVirtualItem));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.MediaType), nameof(BaseItemEntity.TopParentId), nameof(BaseItemEntity.IsVirtualItem));
        AssertIndex(baseItem, null, nameof(BaseItemEntity.Type), nameof(BaseItemEntity.ParentIndexNumber), nameof(BaseItemEntity.IndexNumber));
        foreach (var propertyName in new[]
                 {
                     nameof(BaseItemEntity.Path),
                     nameof(BaseItemEntity.Name),
                     nameof(BaseItemEntity.CleanName),
                     nameof(BaseItemEntity.PresentationUniqueKey),
                     nameof(BaseItemEntity.SeriesPresentationUniqueKey),
                     nameof(BaseItemEntity.SeriesName),
                     nameof(BaseItemEntity.SortName)
                 })
        {
            AssertIndex(baseItem, "hash", propertyName);
        }

        var people = AssertEntityType<People>(model);
        AssertIndex(people, "hash", nameof(People.Name));
        Assert.Equal("C", people.FindProperty(nameof(People.Name))!.GetCollation());

        var streams = AssertEntityType<MediaStreamInfo>(model);
        AssertIndex(streams, null, nameof(MediaStreamInfo.StreamType), nameof(MediaStreamInfo.ItemId), nameof(MediaStreamInfo.Language), nameof(MediaStreamInfo.IsExternal));
        Assert.Equal(64, streams.FindProperty(nameof(MediaStreamInfo.Language))!.GetMaxLength());

        var devices = AssertEntityType<Device>(model);
        AssertIndex(devices, null, nameof(Device.DeviceId), nameof(Device.DateLastActivity));
        AssertIndex(devices, null, nameof(Device.AccessToken), nameof(Device.DateLastActivity));
        AssertIndex(devices, null, nameof(Device.UserId), nameof(Device.DeviceId));

        var providers = AssertEntityType<BaseItemProvider>(model);
        AssertIndex(providers, null, nameof(BaseItemProvider.ProviderId), nameof(BaseItemProvider.ItemId));
        AssertIndex(providers, "hash", nameof(BaseItemProvider.ProviderValue));

        var itemValues = AssertEntityType<ItemValue>(model);
        AssertIndex(itemValues, null, nameof(ItemValue.Type));
        AssertIndex(itemValues, "hash", nameof(ItemValue.CleanValue));
        Assert.True(AssertIndex(itemValues, null, nameof(ItemValue.Type), "ValueDigest").IsUnique);
        Assert.Null(itemValues.FindProperty(nameof(ItemValue.Value))!.GetMaxLength());

        var customPreferences = AssertEntityType<CustomItemDisplayPreferences>(model);
        Assert.True(AssertIndex(
            customPreferences,
            null,
            nameof(CustomItemDisplayPreferences.UserId),
            nameof(CustomItemDisplayPreferences.ItemId),
            nameof(CustomItemDisplayPreferences.Client),
            "KeyDigest").IsUnique);
        Assert.Null(customPreferences.FindProperty(nameof(CustomItemDisplayPreferences.Key))!.GetMaxLength());

        var activity = AssertEntityType<ActivityLog>(model);
        Assert.Equal("C", activity.FindProperty(nameof(ActivityLog.Name))!.GetCollation());
        Assert.Equal("C", activity.FindProperty(nameof(ActivityLog.Overview))!.GetCollation());
        Assert.Equal("C", activity.FindProperty(nameof(ActivityLog.ShortOverview))!.GetCollation());
        Assert.Equal("C", activity.FindProperty(nameof(ActivityLog.Type))!.GetCollation());
        var users = AssertEntityType<User>(model);
        Assert.Equal("C", users.FindProperty(nameof(User.Username))!.GetCollation());
        Assert.Equal("C", users.FindProperty(nameof(User.NormalizedUsername))!.GetCollation());
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
    public async Task ArbitraryUniqueTextValues_AreNotTruncatedOrRejectedByPostgreSqlIndexes()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        var database = await _fixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var userId = Guid.Empty;
        var itemId = Guid.NewGuid();
        var longValue = "metadata-" + new string('x', 12_000);
        var longPreferenceKey = "preference-" + new string('y', 12_000);
        await using (context.ConfigureAwait(true))
        {
            var user = new User("long-values", "test-auth", "test-reset");
            userId = user.Id;
            context.Users.Add(user);
            context.ItemValues.Add(CreateItemValue(longValue));
            context.CustomItemDisplayPreferences.Add(
                new CustomItemDisplayPreferences(user.Id, itemId, "test-client", longPreferenceKey, "value"));

            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            context.ChangeTracker.Clear();

            Assert.Equal(
                longValue,
                await context.ItemValues.Select(value => value.Value).SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal(
                longPreferenceKey,
                await context.CustomItemDisplayPreferences.Select(preference => preference.Key).SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        }

        await using (var duplicateItemValueContext = database.CreateDbContext())
        {
            duplicateItemValueContext.ItemValues.Add(CreateItemValue(longValue));
            await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicateItemValueContext.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }

        await using var duplicatePreferenceContext = database.CreateDbContext();
        duplicatePreferenceContext.CustomItemDisplayPreferences.Add(
            new CustomItemDisplayPreferences(userId, itemId, "test-client", longPreferenceKey, "other"));
        await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicatePreferenceContext.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Fact]
    public async Task PostgreSqlPlanner_UsesRepresentativeProviderSpecificIndexes()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            var cleanNamePlan = await ExplainAsync(
                context,
                "SELECT \"Id\" FROM \"BaseItems\" WHERE \"CleanName\" = 'needle'").ConfigureAwait(true);
            var devicePlan = await ExplainAsync(
                context,
                "SELECT \"Id\" FROM \"Devices\" WHERE \"DeviceId\" = 'device' ORDER BY \"DateLastActivity\" DESC").ConfigureAwait(true);
            var streamPlan = await ExplainAsync(
                context,
                "SELECT \"ItemId\" FROM \"MediaStreamInfos\" WHERE \"StreamType\" = 0 AND \"Language\" = 'eng' AND NOT \"IsExternal\"").ConfigureAwait(true);

            Assert.Contains("IX_BaseItems_CleanName", cleanNamePlan, StringComparison.Ordinal);
            Assert.Contains("IX_Devices_DeviceId_DateLastActivity", devicePlan, StringComparison.Ordinal);
            Assert.Contains("IX_MediaStreamInfos_StreamType_ItemId_Language_IsExternal", streamPlan, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ActivityAndUsernameCaseRules_UseColumnCollationInsteadOfDatabaseLocale()
    {
        var context = await CreateSchemaAsync().ConfigureAwait(true);
        await using (context.ConfigureAwait(true))
        {
            var collations = await GetTextColumnCollationsAsync(context).ConfigureAwait(true);
            Assert.Equal("C", collations[("ActivityLogs", "Name")]);
            Assert.Equal("C", collations[("ActivityLogs", "Overview")]);
            Assert.Equal("C", collations[("ActivityLogs", "ShortOverview")]);
            Assert.Equal("C", collations[("ActivityLogs", "Type")]);
            Assert.Equal("C", collations[("Users", "Username")]);
            Assert.Equal("C", collations[("Users", "NormalizedUsername")]);

            var caseFolds = await GetTurkishAndOrdinalCaseFoldsAsync(context).ConfigureAwait(true);
            Assert.NotEqual(caseFolds.Turkish, caseFolds.Ordinal);
            Assert.Equal("i", caseFolds.Ordinal);

            context.ActivityLogs.Add(new ActivityLog("INDEX FINISHED", "LIBRARY", Guid.Empty));
            context.Users.Add(new User("CaseUser", "test-auth", "test-reset"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var database = await _fixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var manager = new ActivityManager(CreateDbContextFactory(database));
        Assert.Single((await manager.GetPagedResultAsync(new ActivityLogQuery { Name = "index" }).ConfigureAwait(true)).Items);

        await using var duplicateContext = database.CreateDbContext();
        duplicateContext.Users.Add(new User("caseuser", "test-auth", "test-reset"));
        await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicateContext.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
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

    private static IReadOnlyEntityType AssertEntityType<TEntity>(IReadOnlyModel model)
        => Assert.IsAssignableFrom<IReadOnlyEntityType>(model.FindEntityType(typeof(TEntity)));

    private static IReadOnlyIndex AssertIndex(IReadOnlyEntityType entityType, string? method, params string[] propertyNames)
    {
        var index = Assert.Single(
            entityType.GetIndexes(),
            candidate => candidate.Properties.Select(property => property.Name).SequenceEqual(propertyNames));
        Assert.Equal(method, index.GetMethod());
        return index;
    }

    private static async Task<string> ExplainAsync(JellyfinDbContext context, string sql)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using (var disableSequentialScans = connection.CreateCommand())
        {
            disableSequentialScans.CommandText = "SET enable_seqscan = off";
            await disableSequentialScans.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN (COSTS OFF) " + sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var lines = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<Dictionary<(string Table, string Column), string>> GetTextColumnCollationsAsync(JellyfinDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.relname, a.attname, coll.collname "
            + "FROM pg_attribute a "
            + "JOIN pg_class c ON c.oid = a.attrelid "
            + "JOIN pg_namespace n ON n.oid = c.relnamespace "
            + "JOIN pg_collation coll ON coll.oid = a.attcollation "
            + "WHERE n.nspname = 'public' AND c.relname IN ('ActivityLogs', 'Users')";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var result = new Dictionary<(string Table, string Column), string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            result[(reader.GetString(0), reader.GetString(1))] = reader.GetString(2);
        }

        return result;
    }

    private static async Task<(string Turkish, string Ordinal)> GetTurkishAndOrdinalCaseFoldsAsync(JellyfinDbContext context)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT lower('I' COLLATE \"tr-x-icu\"), lower('I' COLLATE \"C\")";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        return (reader.GetString(0), reader.GetString(1));
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
