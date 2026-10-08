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
    /// The provider predates explicit capability declarations.
    /// </summary>
    /// <remarks>
    /// Consumers may preserve non-destructive legacy behavior for these providers, but must not treat this value as
    /// an affirmative capability declaration. New and updated providers should return either <see cref="None"/> or
    /// the operations they explicitly support.
    /// </remarks>
    Unknown = 1 << 3,

    /// <summary>
    /// All optional operations currently defined by Jellyfin.
    /// </summary>
    All = FastMigrationBackup | FullSystemBackup | FullSystemRestore
}
