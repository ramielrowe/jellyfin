using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Activity;
using Jellyfin.Server.Implementations.Tests.Data.PostgreSql;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Querying;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

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
    public async Task UsersPermissionsPreferences_EnforceRelationshipsAndOptimisticConcurrency(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var user = new User("MíXeD-用户", "test-auth", "test-reset");
        user.Permissions.Add(new Permission(PermissionKind.EnableMediaPlayback, true));
        user.Preferences.Add(new Preference(PreferenceKind.AllowedTags, "Kids,音楽"));

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.Users.Add(user);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using (var read = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            var stored = await read.Users
                .AsNoTracking()
                .Include(entity => entity.Permissions)
                .Include(entity => entity.Preferences)
                .SingleAsync(entity => entity.NormalizedUsername == "MÍXED-用户", TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal("MíXeD-用户", stored.Username);
            Assert.True(Assert.Single(stored.Permissions).Value);
            Assert.Equal("Kids,音楽", Assert.Single(stored.Preferences).Value);
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
    public async Task BaseItems_RetainRelationshipsAndQueryBoundarySemantics(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var parentId = Guid.NewGuid();
        var itemIds = Enumerable.Range(0, 129).Select(_ => Guid.NewGuid()).ToArray();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            var parent = CreateItem(parentId, "Folder", "Library", start, true);
            seed.BaseItems.Add(parent);
            seed.BaseItems.AddRange(itemIds.Select((id, index) => CreateItem(
                id,
                "Movie",
                index == 64 ? "Été 用户" : $"Movie {index:D3}",
                start.AddDays(index),
                false,
                parentId,
                index % 5 == 0 ? null : $"Sort {index:D3}")));
            seed.LinkedChildren.Add(new LinkedChildEntity
            {
                ParentId = parentId,
                ChildId = itemIds[64],
                ChildType = LinkedChildType.Manual,
                SortOrder = 1
            });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using var context = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Empty(await context.BaseItems.WhereOneOrMany(Array.Empty<Guid>(), entity => entity.Id).ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(itemIds[0], Assert.Single(await context.BaseItems.WhereOneOrMany([itemIds[0]], entity => entity.Id).Select(entity => entity.Id).ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)));

        var boundaryIds = itemIds.Take(128).Append(itemIds[^1]).ToArray();
        Assert.Equal(
            boundaryIds.Length,
            await context.BaseItems.WhereOneOrMany(boundaryIds, entity => entity.Id).CountAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));

        var pages = await context.BaseItems
            .Where(entity => entity.Type == "Movie")
            .OrderBy(entity => entity.SortName == null)
            .ThenBy(entity => entity.SortName)
            .ThenBy(entity => entity.Id)
            .Skip(62)
            .Take(5)
            .Select(entity => entity.Id)
            .ToArrayAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(5, pages.Length);
        Assert.Equal(64, await context.BaseItems.CountAsync(entity => entity.StartDate >= start.AddDays(65), TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(itemIds[64], await context.LinkedChildren.Select(entity => entity.ChildId).SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));

        var partitioned = new List<Guid>();
        await foreach (var entity in context.BaseItems
                           .Where(entity => entity.Type == "Movie")
                           .OrderBy(entity => entity.Id)
                           .PartitionEagerAsync(64, cancellationToken: TestContext.Current.CancellationToken))
        {
            partitioned.Add(entity.Id);
        }

        Assert.Equal(129, partitioned.Count);
        Assert.Equal(129, partitioned.Distinct().Count());

        var deleted = await context.BaseItems
            .Where(entity => entity.Type == "Movie" && entity.StartDate < start.AddDays(10))
            .ExecuteDeleteAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(10, deleted);
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task PeopleItemValuesAndUserData_RoundTripUnicodeNullsAndCompositeKeys(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var user = new User("matrix-user", "test-auth", "test-reset");
        var item = CreateItem(Guid.NewGuid(), "Movie", "雪国", DateTime.UtcNow, false);
        var person = new People { Id = Guid.NewGuid(), Name = "Zoë 李", PersonType = null };
        var genre = new ItemValue
        {
            ItemValueId = Guid.NewGuid(),
            Type = ItemValueType.Genre,
            Value = "Sci-Fi 世界",
            CleanValue = "sci-fi 世界"
        };

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.AddRange(user, item, person, genre);
            seed.PeopleBaseItemMap.Add(new PeopleBaseItemMap
            {
                ItemId = item.Id,
                Item = item,
                PeopleId = person.Id,
                People = person,
                Role = "Lead_%_Actor",
                SortOrder = 0
            });
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

        await using var context = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var result = await context.BaseItems
            .AsNoTracking()
            .Where(entity => entity.Id.Equals(item.Id))
            .Select(entity => new BehaviorSnapshot(
                entity.Name!,
                entity.Peoples!.Single().People.Name,
                entity.Peoples!.Single().Role!,
                entity.ItemValues!.Single().ItemValue.Value,
                entity.UserData!.Single().CustomDataKey,
                entity.UserData!.Single().Rating,
                entity.UserData!.Single().Likes,
                entity.UserData!.Single().PlayCount))
            .SingleAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);

        var serialized = JsonSerializer.Serialize(result);
        Assert.Equal(result, JsonSerializer.Deserialize<BehaviorSnapshot>(serialized));
        Assert.Equal("雪国", result.ItemName);
        Assert.Null(result.Rating);
        Assert.Null(result.Likes);
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task MediaStreamsAndChapters_SortReplaceAndBulkDelete(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var item = CreateItem(Guid.NewGuid(), "Movie", "Streams", DateTime.UtcNow, false);

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.BaseItems.Add(item);
            seed.MediaStreamInfos.AddRange(
                new MediaStreamInfo { ItemId = item.Id, Item = item, StreamIndex = 2, StreamType = MediaStreamTypeEntity.Subtitle, Language = null },
                new MediaStreamInfo { ItemId = item.Id, Item = item, StreamIndex = 0, StreamType = MediaStreamTypeEntity.Video, Language = "und" },
                new MediaStreamInfo { ItemId = item.Id, Item = item, StreamIndex = 1, StreamType = MediaStreamTypeEntity.Audio, Language = "jpn" });
            seed.Chapters.AddRange(
                new Chapter { ItemId = item.Id, Item = item, ChapterIndex = 1, StartPositionTicks = 20, Name = null },
                new Chapter { ItemId = item.Id, Item = item, ChapterIndex = 0, StartPositionTicks = 0, Name = "序章" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await using var context = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(
            new[] { 0, 1, 2 },
            await context.MediaStreamInfos.OrderBy(entity => entity.StreamIndex).Select(entity => entity.StreamIndex).ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Single(await context.MediaStreamInfos.Where(entity => entity.Language == null).ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        var chapterNames = await context.Chapters
            .OrderBy(entity => entity.StartPositionTicks)
            .Select(entity => entity.Name)
            .ToArrayAsync(TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Collection(
            chapterNames,
            name => Assert.Equal("序章", name),
            Assert.Null);
        Assert.Equal(3, await context.MediaStreamInfos.Where(entity => entity.ItemId.Equals(item.Id)).ExecuteDeleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Empty(await context.MediaStreamInfos.ToArrayAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    [Theory]
    [InlineData(TestProvider.Sqlite)]
    [InlineData(TestProvider.PostgreSql)]
    public async Task ActivityApiKeysAndDevices_FilterPageAndPreserveLiteralLikeCharacters(TestProvider provider)
    {
        await using var database = await CreateDatabaseAsync(provider).ConfigureAwait(true);
        var user = new User("Activity-用户", "test-auth", "test-reset");
        var date = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        await using (var seed = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            seed.Users.Add(user);
            seed.ActivityLogs.AddRange(
                new ActivityLog("Rate 100%_DONE", "Playback", user.Id) { DateCreated = date, Overview = "Mixed CASE" },
                new ActivityLog("ordinary", "Playback", user.Id) { DateCreated = date.AddMinutes(1) },
                new ActivityLog("outside", "System", Guid.Empty) { DateCreated = date.AddDays(-2) });
            seed.ApiKeys.Add(new ApiKey("メディア client") { DateCreated = date, DateLastActivity = date.AddHours(1) });
            seed.Devices.Add(new Device(user.Id, "Jellyfin", "1.0", "Living Room 雪", "device_%_1") { DateLastActivity = date });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var manager = new ActivityManager(database);
        var literal = await manager.GetPagedResultAsync(new ActivityLogQuery
        {
            Name = "100%_done",
            MinDate = date.AddMinutes(-1),
            MaxDate = date.AddMinutes(2),
            Limit = 1
        }).ConfigureAwait(true);
        Assert.Equal(1, literal.TotalRecordCount);
        Assert.Equal("Rate 100%_DONE", Assert.Single(literal.Items).Name);

        await using var context = await database.CreateDbContextAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal("メディア client", await context.ApiKeys.Select(entity => entity.Name).SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("Living Room 雪", await context.Devices.Where(entity => entity.DeviceId == "device_%_1").Select(entity => entity.DeviceName).SingleAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
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

    private sealed record BehaviorSnapshot(
        string ItemName,
        string PersonName,
        string Role,
        string ItemValue,
        string CustomDataKey,
        double? Rating,
        bool? Likes,
        int PlayCount);

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
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var options = new DbContextOptionsBuilder<JellyfinDbContext>()
                .UseSqlite(connection)
                .Options;
            var applicationPaths = Mock.Of<IApplicationPaths>();
            var provider = new SqliteDatabaseProvider(applicationPaths, NullLogger<SqliteDatabaseProvider>.Instance);
            var database = new ProviderDatabase(
                () => new JellyfinDbContext(
                    options,
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
