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
    public async Task GetPagedResultAsync_TextFiltersAreCaseInsensitive()
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
}
