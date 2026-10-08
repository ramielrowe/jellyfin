using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Providers.Sqlite.ValueConverters;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

public sealed class BaseItemRepositoryProviderParityTests : SqliteDbTestFixture
{
    private readonly BaseItemRepository _repository;
    private readonly string _movieType;

    public BaseItemRepositoryProviderParityTests()
    {
        var itemTypeLookup = new ItemTypeLookup();
        _movieType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];
        _repository = CreateBaseItemRepository(itemTypeLookup);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchFilters_MatchOriginalTitleAsciiCaseInsensitively(bool useNameContains)
    {
        var expectedId = Guid.NewGuid();
        Seed(CreateItem(expectedId, "Unrelated", "unrelated", "Unrelated", originalTitle: "THE Hidden TITLE"));

        var query = Query();
        if (useNameContains)
        {
            query.NameContains = "hidden title";
        }
        else
        {
            query.SearchTerm = "hidden title";
        }

        Assert.Equal(expectedId, Assert.Single(_repository.GetItemList(query)).Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchFilters_NonAsciiCaseFoldingIsExplicitlyUnsupported(bool useNameContains)
    {
        Seed(CreateItem(Guid.NewGuid(), "Unrelated", "unrelated", "Unrelated", originalTitle: "ÉTÉ"));

        var query = Query();
        if (useNameContains)
        {
            query.NameContains = "été";
        }
        else
        {
            query.SearchTerm = "été";
        }

        Assert.Empty(_repository.GetItemList(query));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SearchFilters_TreatLikeMetacharactersLiterally(bool useNameContains)
    {
        var expectedId = Guid.NewGuid();
        Seed(
            CreateItem(expectedId, "Literal", "literal", "Literal", originalTitle: @"Rate C:\100%_DONE"),
            CreateItem(Guid.NewGuid(), "Decoy", "decoy", "Decoy", originalTitle: "Rate 100-percent done"));

        var query = Query();
        if (useNameContains)
        {
            query.NameContains = @"C:\100%_done";
        }
        else
        {
            query.SearchTerm = @"C:\100%_done";
        }

        Assert.Equal(expectedId, Assert.Single(_repository.GetItemList(query)).Id);
    }

    [Theory]
    [InlineData(ItemSortBy.SortName)]
    [InlineData(ItemSortBy.DateCreated)]
    [InlineData(ItemSortBy.CommunityRating)]
    public void NullableSortKeys_PlaceNullLastInBothDirections(ItemSortBy sortBy)
    {
        var firstId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var nullId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var first = CreateItem(firstId, "First", "first", "Alpha", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1);
        var second = CreateItem(secondId, "Second", "second", "Zulu", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), 2);
        var nullItem = CreateItem(nullId, "Null", "null", null, null, null);
        Seed(first, second, nullItem);

        var ascending = Query();
        ascending.OrderBy = [(sortBy, SortOrder.Ascending)];
        var descending = Query();
        descending.OrderBy = [(sortBy, SortOrder.Descending)];

        Assert.Equal([firstId, secondId, nullId], _repository.GetItemList(ascending).Select(item => item.Id));
        Assert.Equal([secondId, firstId, nullId], _repository.GetItemList(descending).Select(item => item.Id));
    }

    [Fact]
    public void SharedModelConfiguration_DoesNotOverrideSqliteProviderCustomization()
    {
        using var context = CreateDbContext();
        var entityType = context.Model.FindEntityType(typeof(BaseItemEntity))!;
        var dateCreated = entityType.FindProperty(nameof(BaseItemEntity.DateCreated))!;
        var pathIndex = Assert.Single(entityType.GetIndexes(), index => index.Properties.Count == 1 && index.Properties[0].Name == nameof(BaseItemEntity.Path));

        Assert.IsType<DateTimeKindValueConverter>(dateCreated.GetValueConverter());
        Assert.Null(pathIndex.FindAnnotation("Npgsql:IndexMethod"));
        Assert.Null(entityType.FindProperty(nameof(BaseItemEntity.Type))!.GetMaxLength());
    }

    [Fact]
    public void ArbitraryUniqueTextValues_RemainUnboundedAndRoundTripOnSqlite()
    {
        var longValue = "metadata-" + new string('x', 12_000);
        var longPreferenceKey = "preference-" + new string('y', 12_000);
        using var context = CreateDbContext();
        var user = new User("long-values", "test-auth", "test-reset");
        context.Users.Add(user);
        context.ItemValues.Add(new ItemValue
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = longValue,
            CleanValue = longValue
        });
        context.CustomItemDisplayPreferences.Add(
            new CustomItemDisplayPreferences(user.Id, Guid.NewGuid(), "test-client", longPreferenceKey, "value"));

        context.SaveChanges();
        context.ChangeTracker.Clear();

        Assert.Null(context.Model.FindEntityType(typeof(ItemValue))!.FindProperty(nameof(ItemValue.Value))!.GetMaxLength());
        Assert.Null(context.Model.FindEntityType(typeof(CustomItemDisplayPreferences))!.FindProperty(nameof(CustomItemDisplayPreferences.Key))!.GetMaxLength());
        Assert.Null(context.Model.FindEntityType(typeof(ItemValue))!.FindProperty(nameof(ItemValue.ValueDigest)));
        Assert.Null(context.Model.FindEntityType(typeof(CustomItemDisplayPreferences))!.FindProperty(nameof(CustomItemDisplayPreferences.KeyDigest)));
        Assert.Equal(longValue, Assert.Single(context.ItemValues).Value);
        Assert.Equal(longPreferenceKey, Assert.Single(context.CustomItemDisplayPreferences).Key);
    }

    [Fact]
    public void ProviderIds_RemainUnboundedAndUniqueOnSqlite()
    {
        var item = CreateItem(Guid.NewGuid(), "Provider item", "provider item", "Provider item");
        var providerId = "plugin-provider-" + new string('p', 12_000);
        using (var context = CreateDbContext())
        {
            context.BaseItems.Add(item);
            context.BaseItemProviders.Add(new BaseItemProvider
            {
                ItemId = item.Id,
                Item = item,
                ProviderId = providerId,
                ProviderValue = "external-id"
            });

            context.SaveChanges();
            context.ChangeTracker.Clear();

            Assert.Null(context.Model.FindEntityType(typeof(BaseItemProvider))!.FindProperty(nameof(BaseItemProvider.ProviderIdDigest)));
            Assert.Equal(providerId, Assert.Single(context.BaseItemProviders).ProviderId);
        }

        using var duplicateContext = CreateDbContext();
        duplicateContext.BaseItemProviders.Add(new BaseItemProvider
        {
            ItemId = item.Id,
            Item = null!,
            ProviderId = providerId,
            ProviderValue = "duplicate"
        });
        Assert.Throws<DbUpdateException>(() => duplicateContext.SaveChanges());
    }

    [Fact]
    public void CompositeTextKeys_RemainUnboundedAndUseOriginalTextOnSqlite()
    {
        var itemId = Guid.NewGuid();
        var peopleId = Guid.NewGuid();
        var rolePrefix = new string('r', 4_000);
        var role = rolePrefix + "-first";
        var distinctRole = rolePrefix + "-second";
        var keyPrefix = new string('k', 12_000);
        var customDataKey = keyPrefix + "-first";
        var distinctCustomDataKey = keyPrefix + "-second";
        Guid userId;
        using (var context = CreateDbContext())
        {
            var item = CreateItem(itemId, "Composite key item", "composite key item", "Composite key item");
            var person = new People { Id = peopleId, Name = "Long Role", PersonType = "Actor" };
            var user = new User("long-key-user", "test-auth", "test-reset");
            userId = user.Id;
            context.BaseItems.Add(item);
            context.Peoples.Add(person);
            context.Users.Add(user);
            context.PeopleBaseItemMap.AddRange(
                new PeopleBaseItemMap
                {
                    ItemId = itemId,
                    Item = item,
                    PeopleId = peopleId,
                    People = person,
                    Role = role,
                    SortOrder = 1
                },
                new PeopleBaseItemMap
                {
                    ItemId = itemId,
                    Item = item,
                    PeopleId = peopleId,
                    People = person,
                    Role = distinctRole,
                    SortOrder = 10
                });
            context.UserData.AddRange(
                new UserData
                {
                    ItemId = itemId,
                    Item = item,
                    UserId = userId,
                    User = user,
                    CustomDataKey = customDataKey,
                    PlayCount = 1
                },
                new UserData
                {
                    ItemId = itemId,
                    Item = item,
                    UserId = userId,
                    User = user,
                    CustomDataKey = distinctCustomDataKey,
                    PlayCount = 10
                });

            context.SaveChanges();
        }

        using (var verificationContext = CreateDbContext())
        {
            Assert.Null(verificationContext.Model.FindEntityType(typeof(PeopleBaseItemMap))!.FindProperty(nameof(PeopleBaseItemMap.RoleDigest)));
            Assert.Null(verificationContext.Model.FindEntityType(typeof(UserData))!.FindProperty(nameof(UserData.CustomDataKeyDigest)));

            var storedMap = Assert.Single(verificationContext.PeopleBaseItemMap.Where(map => map.Role == role));
            var storedUserData = Assert.Single(verificationContext.UserData.Where(data => data.CustomDataKey == customDataKey));
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(role)), storedMap.RoleDigest);
            Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(customDataKey)), storedUserData.CustomDataKeyDigest);
            Assert.Equal(2, verificationContext.PeopleBaseItemMap.Count());
            Assert.Equal(2, verificationContext.UserData.Count(data => data.UserId.Equals(userId)));

            storedMap.SortOrder = 2;
            storedUserData.PlayCount = 2;
            verificationContext.SaveChanges();
        }

        using (var duplicateMapContext = CreateDbContext())
        {
            duplicateMapContext.PeopleBaseItemMap.Add(new PeopleBaseItemMap
            {
                ItemId = itemId,
                Item = null!,
                PeopleId = peopleId,
                People = null!,
                Role = role
            });
            Assert.Throws<DbUpdateException>(() => duplicateMapContext.SaveChanges());
        }

        using (var duplicateUserDataContext = CreateDbContext())
        {
            duplicateUserDataContext.UserData.Add(new UserData
            {
                ItemId = itemId,
                Item = null!,
                UserId = userId,
                User = null!,
                CustomDataKey = customDataKey
            });
            Assert.Throws<DbUpdateException>(() => duplicateUserDataContext.SaveChanges());
        }

        using (var context = CreateDbContext())
        {
            var storedMap = Assert.Single(context.PeopleBaseItemMap.Where(map => map.Role == role));
            var storedUserData = Assert.Single(context.UserData.Where(data => data.CustomDataKey == customDataKey));
            Assert.Equal(2, storedMap.SortOrder);
            Assert.Equal(2, storedUserData.PlayCount);
            context.PeopleBaseItemMap.Remove(storedMap);
            context.UserData.Remove(storedUserData);
            context.SaveChanges();
        }

        using (var context = CreateDbContext())
        {
            Assert.Empty(context.PeopleBaseItemMap.Where(map => map.Role == role));
            Assert.Single(context.PeopleBaseItemMap.Where(map => map.Role == distinctRole));
            Assert.Empty(context.UserData.Where(data => data.CustomDataKey == customDataKey));
            Assert.Single(context.UserData.Where(data => data.CustomDataKey == distinctCustomDataKey));
        }
    }

    private void Seed(params BaseItemEntity[] items)
    {
        using var context = CreateDbContext();
        context.BaseItems.AddRange(items);
        context.SaveChanges();
    }

    private InternalItemsQuery Query()
        => new()
        {
            IncludeItemTypes = [BaseItemKind.Movie],
            IncludeOwnedItems = true
        };

    private BaseItemEntity CreateItem(
        Guid id,
        string name,
        string cleanName,
        string? sortName,
        DateTime? dateCreated = null,
        float? communityRating = null,
        string? originalTitle = null)
        => new()
        {
            Id = id,
            Type = _movieType,
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
}
