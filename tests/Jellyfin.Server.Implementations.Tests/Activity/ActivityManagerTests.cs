using System;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Activity;
using Jellyfin.Server.Implementations.Tests.Item;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Activity;

public sealed class ActivityManagerTests : SqliteDbTestFixture
{
    [Fact]
    public async Task GetPagedResultAsync_AsciiTextFiltersAreCaseInsensitive()
    {
        using (var context = CreateDbContext())
        {
            context.ActivityLogs.Add(new ActivityLog("Library Scan Finished", "ScheduledTask", Guid.Empty)
            {
                Overview = "New MEDIA was discovered",
                ShortOverview = "Scan COMPLETE"
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var manager = new ActivityManager(CreateDbContextFactory());
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
    public async Task GetPagedResultAsync_NonAsciiCaseFoldingIsExplicitlyUnsupported()
    {
        using (var context = CreateDbContext())
        {
            context.ActivityLogs.Add(new ActivityLog("ÉVÉNEMENT", "Unicode", Guid.Empty));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var manager = new ActivityManager(CreateDbContextFactory());
        var result = await manager.GetPagedResultAsync(new ActivityLogQuery { Name = "événement" }).ConfigureAwait(true);

        // SQLite's built-in lower() and PostgreSQL's lower() under the C collation only provide
        // the same deterministic folding contract for ASCII.
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetPagedResultAsync_TextFiltersTreatLikeMetacharactersLiterally()
    {
        using (var context = CreateDbContext())
        {
            context.ActivityLogs.AddRange(
                new ActivityLog(@"Progress C:\100%_done", "Literal", Guid.Empty),
                new ActivityLog("Progress 100-percent done", "Literal", Guid.Empty));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        var manager = new ActivityManager(CreateDbContextFactory());
        var result = await manager.GetPagedResultAsync(new ActivityLogQuery
        {
            Name = @"C:\100%_DONE"
        }).ConfigureAwait(true);

        Assert.Equal(@"Progress C:\100%_done", Assert.Single(result.Items).Name);
    }
}
