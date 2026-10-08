namespace Jellyfin.Database.Implementations;

/// <summary>
/// Defines the stable keys for built-in database providers.
/// </summary>
public static class DatabaseProviderKey
{
    /// <summary>
    /// The built-in SQLite provider key and default database type.
    /// </summary>
    public const string Sqlite = "Jellyfin-SQLite";

    /// <summary>
    /// The built-in PostgreSQL provider key.
    /// </summary>
    public const string PostgreSql = "Jellyfin-PgSql";

    /// <summary>
    /// The key used to load a database provider from a plugin.
    /// </summary>
    public const string Plugin = "PLUGIN_PROVIDER";
}
