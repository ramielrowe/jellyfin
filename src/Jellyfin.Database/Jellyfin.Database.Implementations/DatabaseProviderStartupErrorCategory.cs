namespace Jellyfin.Database.Implementations;

/// <summary>
/// Identifies a stable category for a database failure encountered during server startup.
/// </summary>
public enum DatabaseProviderStartupErrorCategory
{
    /// <summary>
    /// The database rejected the configured credentials.
    /// </summary>
    Authentication,

    /// <summary>
    /// The configured database server could not be reached.
    /// </summary>
    Connectivity,

    /// <summary>
    /// The configured database user lacks a required permission.
    /// </summary>
    Permission,

    /// <summary>
    /// The database contains objects that do not match a supported schema state.
    /// </summary>
    IncompatibleSchema
}
