using System;

namespace Jellyfin.Database.Implementations;

/// <summary>
/// Describes the optional backup operations supported by a database provider.
/// </summary>
[Flags]
public enum DatabaseProviderCapabilities
{
    /// <summary>
    /// The provider does not support any optional backup operations.
    /// </summary>
    None = 0,

    /// <summary>
    /// The provider can create, restore, and delete a fast database snapshot for startup migrations.
    /// </summary>
    FastMigrationBackup = 1 << 0,

    /// <summary>
    /// The provider can export its data through the full-system backup service.
    /// </summary>
    FullSystemBackup = 1 << 1,

    /// <summary>
    /// The provider can restore its own data through the full-system backup service.
    /// </summary>
    FullSystemRestore = 1 << 2,

    /// <summary>
    /// All optional operations currently defined by Jellyfin.
    /// </summary>
    All = FastMigrationBackup | FullSystemBackup | FullSystemRestore
}
