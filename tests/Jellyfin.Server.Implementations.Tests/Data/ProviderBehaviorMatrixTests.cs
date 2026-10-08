using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Data;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Activity;
using Jellyfin.Server.Implementations.Item;
using Jellyfin.Server.Implementations.Security;
using Jellyfin.Server.Implementations.Tests.Data.PostgreSql;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Users;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using MediaStream = MediaBrowser.Model.Entities.MediaStream;

namespace Jellyfin.Server.Implementations.Tests.Data;

/// <summary>
/// Runs the same observable persistence contracts against every built-in relational provider.
/// Provider-specific schema assertions live in their provider suites; these tests deliberately
/// compare behavior rather than generated SQL.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ProviderBehaviorMatrixTests
{
    private readonly PostgreSqlDatabaseFixture _postgreSqlFixture;

    public ProviderBehaviorMatrixTests(PostgreSqlDatabaseFixture postgreSqlFixture)
    {
        _postgreSqlFixture = postgreSqlFixture;
    }

    public enum TestProvider
    {
        Sqlite,
        PostgreSql
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task UserAndPreferenceServices_EnforceNormalizedAndCompositeKeys(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        using var userManager = CreateUserManager(database);
        var user = await userManager.CreateUserAsync("MíXeD-用户").ConfigureAwait(true);
        await userManager.UpdatePolicyAsync(user.Id, new UserPolicy
        {
            AuthenticationProviderId = user.AuthenticationProviderId,
            PasswordResetProviderId = user.PasswordResetProviderId,
            EnableMediaPlayback = false,
            AllowedTags = ["Kids", "音楽"],
            BlockedTags = ["Spoiler"]
        }).ConfigureAwait(true);
        await userManager.UpdateConfigurationAsync(user.Id, new UserConfiguration
        {
            AudioLanguagePreference = "jpn",
            OrderedViews = [Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
        }).ConfigureAwait(true);

        var stored = userManager.GetUserByName("míxed-用户");
        Assert.NotNull(stored);
        Assert.Equal(user.Id, stored.Id);
        Assert.Equal("MíXeD-用户", stored.Username);
        Assert.Equal("MÍXED-用户", stored.NormalizedUsername);
        Assert.False(stored.HasPermission(PermissionKind.EnableMediaPlayback));
        Assert.Equal(["Kids", "音楽"], stored.GetPreferenceValues<string>(PreferenceKind.AllowedTags));
        Assert.Equal("jpn", stored.AudioLanguagePreference);

        var preferences = new DisplayPreferencesManager(database);
        var itemId = Guid.NewGuid();
        var display = preferences.GetDisplayPreferences(user.Id, itemId, "matrix");
        display.ShowSidebar = true;
        preferences.UpdateDisplayPreferences(display);
        preferences.SetCustomItemDisplayPreferences(user.Id, itemId, "matrix", new Dictionary<string, string?>
        {
            ["theme"] = "雪",
            ["nullable"] = null
        });
        Assert.True(preferences.GetDisplayPreferences(user.Id, itemId, "matrix").ShowSidebar);
        Assert.Equal(
            new Dictionary<string, string?> { ["nullable"] = null, ["theme"] = "雪" },
            preferences.ListCustomItemDisplayPreferences(user.Id, itemId, "matrix"));

        await using (var duplicate = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            duplicate.Users.Add(new User("míxed-用户", "test-auth", "test-reset"));
            await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicate.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }

        await using (var duplicatePermission = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            duplicatePermission.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true) { UserId = user.Id });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicatePermission.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }

        await using (var duplicatePreference = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            duplicatePreference.Preferences.Add(new Preference(PreferenceKind.AllowedTags, "duplicate") { UserId = user.Id });
            await Assert.ThrowsAsync<DbUpdateException>(
                () => duplicatePreference.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }

        await using var firstWriter = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using var staleWriter = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var firstCopy = await firstWriter.Users.SingleAsync(entity => entity.Id.Equals(user.Id), TestContext.Current.CancellationToken).ConfigureAwait(true);
        var staleCopy = await staleWriter.Users.SingleAsync(entity => entity.Id.Equals(user.Id), TestContext.Current.CancellationToken).ConfigureAwait(true);
        firstCopy.Username = "first-writer";
        await firstWriter.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        staleCopy.Username = "stale-writer";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => staleWriter.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task BaseItemRepository_OrdersPagesCountsAndFiltersRelationships(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var itemTypeLookup = new ItemTypeLookup();
        var movieType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];
        var parentId = Guid.NewGuid();
        var itemIds = Enumerable.Range(1, 129)
            .Select(index => Guid.Parse($"{index:x8}-0000-0000-0000-000000000000"))
            .ToArray();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            var parent = CreateItem(parentId, itemTypeLookup.BaseItemKindNames[BaseItemKind.Folder], "Library", start, true);
            seed.BaseItems.Add(parent);
            seed.BaseItems.AddRange(itemIds.Select((id, index) => CreateItem(
                id,
                movieType,
                index == 64 ? "Été 用户" : $"Movie {index:D3}",
                start.AddDays(index),
                false,
                parentId,
                index >= 125 ? null : index is 62 or 63 ? "Sort tie" : $"Sort {index:D3}")));
            seed.LinkedChildren.Add(new LinkedChildEntity
            {
                ParentId = parentId,
                ChildId = itemIds[64],
                ChildType = Jellyfin.Database.Implementations.Entities.LinkedChildType.Manual,
                SortOrder = 1
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var repository = CreateBaseItemRepository(database, itemTypeLookup);
        var expectedAscending = itemIds
            .Select((id, index) => new { Id = id, Name = index == 64 ? "Été 用户" : $"Movie {index:D3}", SortName = index >= 125 ? null : index is 62 or 63 ? "Sort tie" : $"Sort {index:D3}" })
            .OrderBy(item => item.SortName is null)
            .ThenBy(item => item.SortName, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToArray();
        var expectedDescending = itemIds
            .Select((id, index) => new { Id = id, Name = index == 64 ? "Été 用户" : $"Movie {index:D3}", SortName = index >= 125 ? null : index is 62 or 63 ? "Sort tie" : $"Sort {index:D3}" })
            .OrderBy(item => item.SortName is null)
            .ThenByDescending(item => item.SortName, StringComparer.Ordinal)
            .ThenByDescending(item => item.Name, StringComparer.Ordinal)
            .Select(item => item.Id)
            .ToArray();

        var ascending = ItemQuery(BaseItemKind.Movie, (ItemSortBy.SortName, SortOrder.Ascending));
        var descending = ItemQuery(BaseItemKind.Movie, (ItemSortBy.SortName, SortOrder.Descending));
        Assert.Equal(expectedAscending, repository.GetItemIdsList(ascending));
        Assert.Equal(expectedDescending, repository.GetItemIdsList(descending));
        Assert.Equal(expectedAscending[^4..], repository.GetItemIdsList(ascending).TakeLast(4));
        Assert.Equal(expectedDescending[^4..], repository.GetItemIdsList(descending).TakeLast(4));

        var firstPageQuery = ItemQuery(BaseItemKind.Movie, (ItemSortBy.SortName, SortOrder.Ascending));
        firstPageQuery.StartIndex = 62;
        firstPageQuery.Limit = 5;
        firstPageQuery.EnableTotalRecordCount = true;
        var firstPage = repository.GetItems(firstPageQuery);
        Assert.Equal(129, firstPage.TotalRecordCount);
        Assert.Equal(62, firstPage.StartIndex);
        Assert.Equal(expectedAscending[62..67], firstPage.Items.Select(item => item.Id));

        var adjacentPageQuery = ItemQuery(BaseItemKind.Movie, (ItemSortBy.SortName, SortOrder.Ascending));
        adjacentPageQuery.StartIndex = 67;
        adjacentPageQuery.Limit = 5;
        adjacentPageQuery.EnableTotalRecordCount = true;
        var adjacentPage = repository.GetItems(adjacentPageQuery);
        Assert.Equal(129, adjacentPage.TotalRecordCount);
        Assert.Equal(expectedAscending[67..72], adjacentPage.Items.Select(item => item.Id));
        Assert.Empty(firstPage.Items.Select(item => item.Id).Intersect(adjacentPage.Items.Select(item => item.Id)));

        var children = ItemQuery(BaseItemKind.Movie, (ItemSortBy.DateCreated, SortOrder.Ascending));
        children.ParentId = parentId;
        Assert.Equal(itemIds, repository.GetItemIdsList(children));
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task PeopleRepositoryAndRelatedData_ReturnCompleteSnapshots(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var itemTypeLookup = new ItemTypeLookup();
        var user = new User("matrix-user", "test-auth", "test-reset");
        var item = CreateItem(Guid.NewGuid(), itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie], "雪国", DateTime.UtcNow, false);
        var genre = new ItemValue
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = "Sci-Fi 世界",
            CleanValue = "sci-fi 世界"
        };

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.AddRange(user, item, genre);
            seed.ItemValuesMap.Add(new ItemValueMap
            {
                ItemId = item.Id,
                Item = item,
                ItemValueId = genre.ItemValueId,
                ItemValue = genre
            });
            seed.UserData.Add(new UserData
            {
                ItemId = item.Id,
                Item = item,
                UserId = user.Id,
                User = user,
                CustomDataKey = "custom/用户",
                Rating = null,
                Likes = null,
                IsFavorite = true,
                PlayCount = 3
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var peopleRepository = new PeopleRepository(database, itemTypeLookup, Mock.Of<IItemQueryHelpers>());
        peopleRepository.UpdatePeople(
            item.Id,
            [
                new PersonInfo { Name = "Zoë 李", Type = PersonKind.Actor, Role = "Lead_%_Actor", SortOrder = 7 },
                new PersonInfo { Name = "zoë 李", Type = PersonKind.Actor, Role = "lead_%_actor", SortOrder = 99 },
                new PersonInfo { Name = "Renée", Type = PersonKind.Director, Role = string.Empty, SortOrder = 2 }
            ]);

        var people = peopleRepository.GetPeople(new InternalPeopleQuery
        {
            ItemId = item.Id,
            EnableTotalRecordCount = true,
            Limit = 10
        });
        Assert.Equal(2, people.TotalRecordCount);
        Assert.Collection(
            people.Items,
            person =>
            {
                Assert.Equal("Zoë 李", person.Name);
                Assert.Equal(PersonKind.Actor, person.Type);
                Assert.Equal("Lead_%_Actor", person.Role);
                Assert.Equal(7, person.SortOrder);
            },
            person =>
            {
                Assert.Equal("Renée", person.Name);
                Assert.Equal(PersonKind.Director, person.Type);
                Assert.Equal(string.Empty, person.Role);
                Assert.Equal(2, person.SortOrder);
            });

        await using var context = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var storedItem = await context.BaseItems
            .AsNoTracking()
            .Include(entity => entity.Peoples!)
            .ThenInclude(map => map.People)
            .Include(entity => entity.ItemValues!)
            .ThenInclude(map => map.ItemValue)
            .Include(entity => entity.UserData!)
            .Where(entity => entity.Id.Equals(item.Id))
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal("雪国", storedItem.Name);
        Assert.Collection(
            storedItem.Peoples!.OrderBy(map => map.ListOrder),
            map =>
            {
                Assert.Equal("Zoë 李", map.People.Name);
                Assert.Equal(nameof(PersonKind.Actor), map.People.PersonType);
                Assert.Equal("Lead_%_Actor", map.Role);
                Assert.Equal(7, map.SortOrder);
                Assert.Equal(0, map.ListOrder);
            },
            map =>
            {
                Assert.Equal("Renée", map.People.Name);
                Assert.Equal(nameof(PersonKind.Director), map.People.PersonType);
                Assert.Equal(string.Empty, map.Role);
                Assert.Equal(2, map.SortOrder);
                Assert.Equal(1, map.ListOrder);
            });
        var storedValue = Assert.Single(storedItem.ItemValues!).ItemValue;
        Assert.Equal(ItemValueType.Genre, storedValue.Type);
        Assert.Equal("Sci-Fi 世界", storedValue.Value);
        Assert.Equal("sci-fi 世界", storedValue.CleanValue);
        var storedUserData = Assert.Single(storedItem.UserData!);
        Assert.Equal(user.Id, storedUserData.UserId);
        Assert.Equal("custom/用户", storedUserData.CustomDataKey);
        Assert.Null(storedUserData.Rating);
        Assert.Null(storedUserData.Likes);
        Assert.True(storedUserData.IsFavorite);
        Assert.Equal(3, storedUserData.PlayCount);
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task MediaAndChapterRepositories_SaveReplaceGetDeleteAndPreserveOtherItems(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var item = CreateItem(Guid.NewGuid(), new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie], "Streams", DateTime.UtcNow, false);
        item.Path = "/media/item.mkv";
        var otherItem = CreateItem(Guid.NewGuid(), new ItemTypeLookup().BaseItemKindNames[BaseItemKind.Movie], "Other", DateTime.UtcNow, false);
        otherItem.Path = "/media/other.mkv";

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.BaseItems.AddRange(item, otherItem);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(host => host.ReverseVirtualPath(It.IsAny<string>())).Returns((string path) => path);
        appHost.Setup(host => host.ExpandVirtualPath(It.IsAny<string>())).Returns((string path) => path);
        var localization = new Mock<ILocalizationManager>();
        var streams = new MediaStreamRepository(database, appHost.Object, localization.Object);
        var chapters = new ChapterRepository(database, Mock.Of<IImageProcessor>());

        streams.SaveMediaStreams(
            item.Id,
            [
                new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, Language = null, Title = "旧字幕" },
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 1920 },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Language = "jpn", Channels = 2 }
            ],
            TestContext.Current.CancellationToken);
        streams.SaveMediaStreams(
            otherItem.Id,
            [new MediaStream { Index = 9, Type = MediaStreamType.Audio, Language = "eng", Title = "preserved" }],
            TestContext.Current.CancellationToken);
        chapters.SaveChapters(
            item.Id,
            [
                new ChapterInfo { StartPositionTicks = 0, Name = "序章" },
                new ChapterInfo { StartPositionTicks = 20, Name = null }
            ]);
        chapters.SaveChapters(otherItem.Id, [new ChapterInfo { StartPositionTicks = 99, Name = "preserved" }]);

        Assert.Equal([0, 1, 2], streams.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }).Select(stream => stream.Index));
        Assert.Collection(
            chapters.GetChapters(item.Id),
            chapter =>
            {
                Assert.Equal(0, chapter.StartPositionTicks);
                Assert.Equal("序章", chapter.Name);
            },
            chapter =>
            {
                Assert.Equal(20, chapter.StartPositionTicks);
                Assert.Null(chapter.Name);
            });

        Assert.Throws<InvalidOperationException>(() => streams.SaveMediaStreams(
            item.Id,
            [
                new MediaStream { Index = 8, Type = MediaStreamType.Audio },
                new MediaStream { Index = 8, Type = MediaStreamType.Subtitle }
            ],
            TestContext.Current.CancellationToken));
        Assert.Equal([0, 1, 2], streams.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }).Select(stream => stream.Index));

        streams.SaveMediaStreams(
            item.Id,
            [
                new MediaStream { Index = 4, Type = MediaStreamType.Audio, Language = "fra", Channels = 6, Title = "replacement" },
                new MediaStream { Index = 3, Type = MediaStreamType.Video, Codec = "hevc", Width = 3840 }
            ],
            TestContext.Current.CancellationToken);
        chapters.SaveChapters(item.Id, [new ChapterInfo { StartPositionTicks = 10, Name = "replacement" }]);

        var replacedStreams = streams.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
        Assert.Collection(
            replacedStreams,
            stream =>
            {
                Assert.Equal(3, stream.Index);
                Assert.Equal("hevc", stream.Codec);
                Assert.Equal(3840, stream.Width);
            },
            stream =>
            {
                Assert.Equal(4, stream.Index);
                Assert.Equal("fra", stream.Language);
                Assert.Equal(6, stream.Channels);
                Assert.Equal("replacement", stream.Title);
            });
        var replacedChapter = Assert.Single(chapters.GetChapters(item.Id));
        Assert.Equal(10, replacedChapter.StartPositionTicks);
        Assert.Equal("replacement", replacedChapter.Name);
        Assert.Equal("preserved", Assert.Single(streams.GetMediaStreams(new MediaStreamQuery { ItemId = otherItem.Id })).Title);
        Assert.Equal("preserved", Assert.Single(chapters.GetChapters(otherItem.Id)).Name);

        streams.SaveMediaStreams(item.Id, [], TestContext.Current.CancellationToken);
        await chapters.DeleteChaptersAsync(item.Id, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Empty(streams.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id }));
        Assert.Empty(chapters.GetChapters(item.Id));
        Assert.Single(streams.GetMediaStreams(new MediaStreamQuery { ItemId = otherItem.Id }));
        Assert.Single(chapters.GetChapters(otherItem.Id));
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task ActivityAndAuthenticationServices_FilterPageAndPersistIndependentWrites(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var user = new User("Activity-用户", "test-auth", "test-reset");
        var date = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.Users.Add(user);
            seed.ActivityLogs.AddRange(
                new ActivityLog(@"Literal C:\100%_done alpha", "Playback", user.Id) { DateCreated = date, Overview = "Mixed CASE" },
                new ActivityLog(@"Literal C:\100%_done beta", "Playback", user.Id) { DateCreated = date.AddMinutes(1) },
                new ActivityLog(@"Literal C:\100%_done gamma", "Playback", user.Id) { DateCreated = date.AddMinutes(2) },
                new ActivityLog(@"Literal C:\100%_done omega", "Playback", user.Id) { DateCreated = date.AddMinutes(3) },
                new ActivityLog(@"Decoy C:\100-percent-done", "Playback", user.Id) { DateCreated = date.AddMinutes(4) },
                new ActivityLog(@"Decoy C:\100X_done", "Playback", user.Id) { DateCreated = date.AddMinutes(5) },
                new ActivityLog(@"Decoy C:/100%_done", "Playback", user.Id) { DateCreated = date.AddMinutes(6) },
                new ActivityLog("outside", "System", Guid.Empty) { DateCreated = date.AddDays(-2) });
            seed.Devices.Add(new Device(user.Id, "Jellyfin", "1.0", "Living Room 雪", "device_%_1") { DateLastActivity = date });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var manager = new ActivityManager(database);
        var literal = await manager.GetPagedResultAsync(new ActivityLogQuery
        {
            Name = @"C:\100%_done",
            MinDate = date.AddMinutes(-1),
            MaxDate = date.AddMinutes(10),
            Skip = 0,
            Limit = 2,
            OrderBy = [(ActivityLogSortBy.DateCreated, SortOrder.Ascending)]
        }).ConfigureAwait(true);
        Assert.Equal(4, literal.TotalRecordCount);
        Assert.Equal(
            [@"Literal C:\100%_done alpha", @"Literal C:\100%_done beta"],
            literal.Items.Select(entry => entry.Name));

        var adjacent = await manager.GetPagedResultAsync(new ActivityLogQuery
        {
            Name = @"C:\100%_done",
            MinDate = date.AddMinutes(-1),
            MaxDate = date.AddMinutes(10),
            Skip = 2,
            Limit = 2,
            OrderBy = [(ActivityLogSortBy.DateCreated, SortOrder.Ascending)]
        }).ConfigureAwait(true);
        Assert.Equal(4, adjacent.TotalRecordCount);
        Assert.Equal(
            [@"Literal C:\100%_done gamma", @"Literal C:\100%_done omega"],
            adjacent.Items.Select(entry => entry.Name));

        var authentication = new AuthenticationManager(database);
        var createdKeys = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(index => authentication.CreateApiKey($"matrix-{index}"))).ConfigureAwait(true);
        Assert.Equal(6, createdKeys.Select(key => key.AccessToken).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Enumerable.Range(0, 6).Select(index => $"matrix-{index}").Order(StringComparer.Ordinal),
            (await authentication.GetApiKeys().ConfigureAwait(true)).Select(key => key.AppName).Order(StringComparer.Ordinal));
        await authentication.DeleteApiKey(createdKeys[2].AccessToken).ConfigureAwait(true);
        Assert.DoesNotContain(
            await authentication.GetApiKeys().ConfigureAwait(true),
            key => key.AccessToken == createdKeys[2].AccessToken);

        await using var context = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("Living Room 雪", await context.Devices.Where(entity => entity.DeviceId == "device_%_1").Select(entity => entity.DeviceName).SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task BaseItemRepository_LiteralLikeFiltersReturnExactOrderedPages(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var itemTypeLookup = new ItemTypeLookup();
        var movieType = itemTypeLookup.BaseItemKindNames[BaseItemKind.Movie];
        var literalIds = new[]
        {
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("10000000-0000-0000-0000-000000000002"),
            Guid.Parse("10000000-0000-0000-0000-000000000003"),
            Guid.Parse("10000000-0000-0000-0000-000000000004")
        };

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.BaseItems.AddRange(
                literalIds.Select((id, index) => CreateSearchItem(id, movieType, $@"Literal C:\100%_done {index}", $"literal-{index:D2}"))
                    .Concat(
                    [
                        CreateSearchItem(Guid.NewGuid(), movieType, @"Decoy C:\100-percent-done", "decoy-01"),
                        CreateSearchItem(Guid.NewGuid(), movieType, @"Decoy C:\100X_done", "decoy-02"),
                        CreateSearchItem(Guid.NewGuid(), movieType, @"Decoy C:/100%_done", "decoy-03")
                    ]));
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var repository = CreateBaseItemRepository(database, itemTypeLookup);
        var firstQuery = ItemQuery(BaseItemKind.Movie, (ItemSortBy.SortName, SortOrder.Ascending));
        firstQuery.NameContains = @"C:\100%_done";
        firstQuery.StartIndex = 0;
        firstQuery.Limit = 2;
        firstQuery.EnableTotalRecordCount = true;
        var first = repository.GetItems(firstQuery);
        Assert.Equal(4, first.TotalRecordCount);
        Assert.Equal(literalIds[..2], first.Items.Select(item => item.Id));

        var secondQuery = ItemQuery(BaseItemKind.Movie, (ItemSortBy.SortName, SortOrder.Ascending));
        secondQuery.NameContains = @"C:\100%_done";
        secondQuery.StartIndex = 2;
        secondQuery.Limit = 2;
        secondQuery.EnableTotalRecordCount = true;
        var second = repository.GetItems(secondQuery);
        Assert.Equal(4, second.TotalRecordCount);
        Assert.Equal(literalIds[2..], second.Items.Select(item => item.Id));

        var descending = ItemQuery(BaseItemKind.Movie, (ItemSortBy.SortName, SortOrder.Descending));
        descending.NameContains = @"C:\100%_done";
        Assert.Equal(literalIds.Reverse(), repository.GetItemIdsList(descending));
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task ItemPersistenceService_DeletesThousandsWithDuplicatesAndPreservesUnrelatedRows(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        const int DeleteCount = 2_050;
        var targetIds = Enumerable.Range(1, DeleteCount)
            .Select(index => Guid.Parse($"{index:x8}-1111-1111-1111-111111111111"))
            .ToArray();
        var survivorId = Guid.Parse("ffffffff-1111-1111-1111-111111111111");
        var user = new User("bulk-user", "test-auth", "test-reset");
        var survivorValue = CreateItemValue("surviving value");
        var removedValue = CreateItemValue("removed value");
        var survivorPerson = new People { Id = Guid.NewGuid(), Name = "Surviving Person", PersonType = nameof(PersonKind.Actor) };
        var removedPerson = new People { Id = Guid.NewGuid(), Name = "Removed Person", PersonType = nameof(PersonKind.Actor) };

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.Users.Add(user);
            seed.BaseItems.AddRange(targetIds.Select((id, index) => CreateItem(id, typeof(Book).FullName!, $"Delete {index}", DateTime.UtcNow, false)));
            seed.BaseItems.Add(CreateItem(survivorId, typeof(Book).FullName!, "Keep", DateTime.UtcNow, false));
            seed.UserData.AddRange(targetIds.Select((id, index) => new UserData
            {
                ItemId = id,
                Item = null!,
                UserId = user.Id,
                User = null!,
                CustomDataKey = "shared-key",
                LastPlayedDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index),
                PlayCount = index
            }));
            seed.UserData.Add(new UserData { ItemId = survivorId, Item = null!, UserId = user.Id, User = null!, CustomDataKey = "survivor", PlayCount = 99 });
            seed.ItemValues.AddRange(removedValue, survivorValue);
            seed.ItemValuesMap.AddRange(
                new ItemValueMap { ItemId = targetIds[0], Item = null!, ItemValueId = removedValue.ItemValueId, ItemValue = null! },
                new ItemValueMap { ItemId = survivorId, Item = null!, ItemValueId = survivorValue.ItemValueId, ItemValue = null! });
            seed.Peoples.AddRange(removedPerson, survivorPerson);
            seed.PeopleBaseItemMap.AddRange(
                new PeopleBaseItemMap { ItemId = targetIds[0], Item = null!, PeopleId = removedPerson.Id, People = null!, Role = "Removed" },
                new PeopleBaseItemMap { ItemId = survivorId, Item = null!, PeopleId = survivorPerson.Id, People = null!, Role = "Survivor" });
            seed.MediaStreamInfos.AddRange(
                new MediaStreamInfo { ItemId = targetIds[0], Item = null!, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video },
                new MediaStreamInfo { ItemId = survivorId, Item = null!, StreamIndex = 7, StreamType = MediaStreamTypeEntity.Audio });
            seed.Chapters.AddRange(
                new Chapter { ItemId = targetIds[0], Item = null!, ChapterIndex = 0, StartPositionTicks = 0, Name = "removed" },
                new Chapter { ItemId = survivorId, Item = null!, ChapterIndex = 0, StartPositionTicks = 7, Name = "survives" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var service = new ItemPersistenceService(database, Mock.Of<IServerApplicationHost>(), NullLogger<ItemPersistenceService>.Instance);
        service.DeleteItem([.. targetIds, targetIds[0], targetIds[^1], Guid.Parse("eeeeeeee-1111-1111-1111-111111111111")]);

        await using var after = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(
            new[] { BaseItemRepository.PlaceholderId, survivorId }.Order(),
            await after.BaseItems.Select(item => item.Id).OrderBy(id => id).ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        var detached = Assert.Single(await after.UserData.Where(data => data.ItemId.Equals(BaseItemRepository.PlaceholderId)).ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(DeleteCount - 1, detached.PlayCount);
        Assert.Equal("shared-key", detached.CustomDataKey);
        var survivorData = Assert.Single(await after.UserData.Where(data => data.ItemId.Equals(survivorId)).ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(99, survivorData.PlayCount);
        Assert.Equal(survivorValue.ItemValueId, Assert.Single(await after.ItemValues.ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ItemValueId);
        Assert.Equal(survivorId, Assert.Single(await after.ItemValuesMap.ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ItemId);
        Assert.Equal("Surviving Person", Assert.Single(await after.Peoples.ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).Name);
        Assert.Equal(survivorId, Assert.Single(await after.PeopleBaseItemMap.ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ItemId);
        Assert.Equal(7, Assert.Single(await after.MediaStreamInfos.ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).StreamIndex);
        Assert.Equal("survives", Assert.Single(await after.Chapters.ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).Name);
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task TransactionsAndUniqueConstraints_RollBackAtomically(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        await using (var context = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            context.Users.Add(new User("rolled-back", "test-auth", "test-reset"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await transaction.RollbackAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using (var verification = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            Assert.False(await verification.Users.AnyAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
            verification.ItemValues.AddRange(CreateItemValue("Duplicate"), CreateItemValue("Duplicate"));
            await Assert.ThrowsAsync<DbUpdateException>(
                () => verification.SaveChangesAsync(TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }
    }

    private async Task<ProviderDatabase> CreateDatabaseAsync(TestProvider provider)
    {
        if (provider == TestProvider.PostgreSql)
        {
            Assert.SkipUnless(_postgreSqlFixture.IsConfigured, _postgreSqlFixture.SkipReason);
            var postgreSql = await _postgreSqlFixture.GetDatabaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await postgreSql.ResetAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            var result = ProviderDatabase.ForPostgreSql(postgreSql);
            await using var context = await result.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            return result;
        }

        return await ProviderDatabase.CreateSqliteAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static InternalItemsQuery ItemQuery(BaseItemKind kind, params (ItemSortBy OrderBy, SortOrder SortOrder)[] orderBy)
        => new()
        {
            IncludeItemTypes = [kind],
            IncludeOwnedItems = true,
            OrderBy = orderBy
        };

    private static BaseItemRepository CreateBaseItemRepository(ProviderDatabase database, ItemTypeLookup itemTypeLookup)
    {
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(manager => manager.Configuration).Returns(new ServerConfiguration());
        return new BaseItemRepository(
            database,
            Mock.Of<IServerApplicationHost>(),
            itemTypeLookup,
            configurationManager.Object,
            NullLogger<BaseItemRepository>.Instance);
    }

    private static UserManager CreateUserManager(ProviderDatabase database)
    {
        var applicationPaths = new Mock<IServerApplicationPaths>();
        applicationPaths.Setup(paths => paths.ProgramDataPath).Returns(Path.GetTempPath());
        var configurationManager = new Mock<IServerConfigurationManager>();
        configurationManager.Setup(manager => manager.ApplicationPaths).Returns(applicationPaths.Object);
        configurationManager.Setup(manager => manager.Configuration).Returns(new ServerConfiguration());
        var applicationHost = Mock.Of<IApplicationHost>();
        var defaultAuthenticationProvider = new DefaultAuthenticationProvider(
            NullLogger<DefaultAuthenticationProvider>.Instance,
            Mock.Of<ICryptoProvider>());
        var defaultPasswordResetProvider = new DefaultPasswordResetProvider(configurationManager.Object, applicationHost);
        return new UserManager(
            database,
            new NoopEventManager(),
            Mock.Of<INetworkManager>(),
            applicationHost,
            Mock.Of<IImageProcessor>(),
            NullLogger<UserManager>.Instance,
            configurationManager.Object,
            [defaultPasswordResetProvider],
            [defaultAuthenticationProvider, new InvalidAuthProvider()]);
    }

    private static BaseItemEntity CreateSearchItem(Guid id, string type, string name, string sortName)
    {
        var item = CreateItem(id, type, name, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), false, sortName: sortName);
        item.OriginalTitle = name;
        return item;
    }

    private static BaseItemEntity CreateItem(
        Guid id,
        string type,
        string name,
        DateTime startDate,
        bool isFolder,
        Guid? parentId = null,
        string? sortName = null)
        => new()
        {
            Id = id,
            Type = type,
            Name = name,
            CleanName = name.ToLowerInvariant(),
            SortName = sortName,
            StartDate = startDate,
            DateCreated = startDate,
            ParentId = parentId,
            TopParentId = parentId,
            IsFolder = isFolder,
            IsVirtualItem = false,
            MediaType = isFolder ? null : "Video",
            PresentationUniqueKey = id.ToString("N")
        };

    private static ItemValue CreateItemValue(string value)
        => new()
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = value,
            CleanValue = value.ToLowerInvariant()
        };

    private sealed class NoopEventManager : IEventManager
    {
        public void Publish<T>(T eventArgs)
            where T : EventArgs
        {
        }

        public Task PublishAsync<T>(T eventArgs)
            where T : EventArgs
            => Task.CompletedTask;
    }

    private sealed class ProviderDatabase : IDbContextFactory<JellyfinDbContext>, IAsyncDisposable
    {
        private readonly Func<JellyfinDbContext> _createContext;
        private readonly SqliteConnection? _sqliteConnection;

        private ProviderDatabase(Func<JellyfinDbContext> createContext, SqliteConnection? sqliteConnection = null)
        {
            _createContext = createContext;
            _sqliteConnection = sqliteConnection;
        }

        public static ProviderDatabase ForPostgreSql(PostgreSqlTestDatabase database)
            => new(database.CreateDbContext);

        public static async Task<ProviderDatabase> CreateSqliteAsync(CancellationToken cancellationToken)
        {
            var connectionString = $"Data Source=provider-matrix-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=30";
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var applicationPaths = Mock.Of<IApplicationPaths>();
            var provider = new SqliteDatabaseProvider(applicationPaths, NullLogger<SqliteDatabaseProvider>.Instance);
            var database = new ProviderDatabase(
                () => new JellyfinDbContext(
                    new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(connectionString).Options,
                    NullLogger<JellyfinDbContext>.Instance,
                    provider,
                    new NoLockBehavior(NullLogger<NoLockBehavior>.Instance)),
                connection);
            await using var context = await database.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
            return database;
        }

        public JellyfinDbContext CreateDbContext() => _createContext();

        public ValueTask<JellyfinDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(CreateDbContext());

        public async ValueTask DisposeAsync()
        {
            if (_sqliteConnection is not null)
            {
                await _sqliteConnection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
