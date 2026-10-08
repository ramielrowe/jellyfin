using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Drops keys that no current property reads or writes from the serialized <c>BaseItems.Data</c> blob.
/// </summary>
[JellyfinMigration("2026-09-11T12:00:00", nameof(StripEmbeddedLinkedChildren))]
internal class StripEmbeddedLinkedChildren : IAsyncMigrationRoutine
{
    internal const int BatchSize = 100;

    private readonly ILogger<StripEmbeddedLinkedChildren> _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;

    public StripEmbeddedLinkedChildren(
        ILoggerFactory loggerFactory,
        IDbContextFactory<JellyfinDbContext> dbProvider)
    {
        _logger = loggerFactory.CreateLogger<StripEmbeddedLinkedChildren>();
        _dbProvider = dbProvider;
    }

    /// <inheritdoc/>
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var contextScope = context.ConfigureAwait(false);

        // Data remains a text column on every provider. Parse it in the application so this historical
        // transform has identical semantics on SQLite and PostgreSQL, including preserving malformed blobs.
        // A fresh PostgreSQL baseline records this pre-baseline migration as inapplicable during setup; this
        // path remains necessary for databases whose code-migration history predates that baseline record.
        var updated = 0;
        Guid? lastId = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = context.BaseItems
                .AsNoTracking()
                .Where(item => item.Data != null
                    && (item.Data.Contains("\"LinkedChildren\"")
                        || item.Data.Contains("\"ExtraIds\"")
                        || item.Data.Contains("\"SupportsExternalTransfer\"")));
            if (lastId.HasValue)
            {
                var boundary = lastId.Value;
                query = query.Where(item => item.Id.CompareTo(boundary) > 0);
            }

            var candidates = await query
                .OrderBy(item => item.Id)
                .Select(item => new { item.Id, item.Data })
                .Take(BatchSize)
                .ToArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            if (candidates.Length == 0)
            {
                break;
            }

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(candidate.Data!);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (node is not JsonObject data)
                {
                    continue;
                }

                var changed = data.Remove("LinkedChildren");
                changed |= data.Remove("ExtraIds");
                changed |= data.Remove("SupportsExternalTransfer");
                if (!changed)
                {
                    continue;
                }

                var item = new BaseItemEntity { Id = candidate.Id, Type = string.Empty, Data = data.ToJsonString() };
                context.Attach(item);
                context.Entry(item).Property(entity => entity.Data).IsModified = true;
                updated++;
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            context.ChangeTracker.Clear();
            lastId = candidates[^1].Id;
        }

        _logger.LogInformation("Dropped dead keys from the serialized data of {Count} items", updated);
    }
}
