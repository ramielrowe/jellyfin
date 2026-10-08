using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Drops keys that no current property reads or writes from the serialized <c>BaseItems.Data</c> blob.
/// </summary>
[JellyfinMigration("2026-09-11T12:00:00", nameof(StripEmbeddedLinkedChildren))]
internal class StripEmbeddedLinkedChildren : IDatabaseMigrationRoutine
{
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
    public void Perform()
    {
        using var context = _dbProvider.CreateDbContext();

        // Data remains a text column on every provider. Parse it in the application so this historical
        // transform has identical semantics on SQLite and PostgreSQL, including preserving malformed blobs.
        // A fresh PostgreSQL baseline records this pre-baseline migration as inapplicable during setup; this
        // path remains necessary for databases whose code-migration history predates that baseline record.
        var candidates = context.BaseItems
            .Where(item => item.Data != null
                && (item.Data.Contains("\"LinkedChildren\"")
                    || item.Data.Contains("\"ExtraIds\"")
                    || item.Data.Contains("\"SupportsExternalTransfer\"")))
            .ToArray();
        var updated = 0;

        foreach (var item in candidates)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(item.Data!);
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

            item.Data = data.ToJsonString();
            updated++;
        }

        context.SaveChanges();

        _logger.LogInformation("Dropped dead keys from the serialized data of {Count} items", updated);
    }
}
