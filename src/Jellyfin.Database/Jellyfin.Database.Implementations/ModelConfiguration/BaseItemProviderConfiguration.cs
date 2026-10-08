using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Jellyfin.Database.Implementations.ModelConfiguration;

/// <summary>
/// BaseItemProvider configuration.
/// </summary>
public class BaseItemProviderConfiguration : IEntityTypeConfiguration<BaseItemProvider>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<BaseItemProvider> builder)
    {
        // PostgreSQL maps the digest explicitly so its primary key can remain safe for arbitrary
        // provider identifiers. SQLite retains its existing schema and key.
        builder.Ignore(e => e.ProviderIdDigest);
        builder.HasKey(e => new { e.ItemId, e.ProviderId });
        builder.HasOne(e => e.Item);
        builder.HasIndex(e => new { e.ProviderId, e.ItemId, e.ProviderValue });
    }
}
