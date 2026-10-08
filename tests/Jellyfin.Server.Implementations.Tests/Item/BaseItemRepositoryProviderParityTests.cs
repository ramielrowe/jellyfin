using System;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Providers.Sqlite.ValueConverters;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
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
    public void SearchFilters_MatchOriginalTitleCaseInsensitively(bool useNameContains)
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
