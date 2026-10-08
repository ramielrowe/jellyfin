using System.Collections.Generic;

namespace Jellyfin.Database.Implementations.DbConfiguration;

/// <summary>
/// Options to configure jellyfins managed database.
/// </summary>
public class DatabaseConfigurationOptions
{
    /// <summary>
    /// Gets or Sets the type of database jellyfin should use.
    /// </summary>
    public required string DatabaseType { get; set; }

    /// <summary>
    /// Gets or sets connection and provider-specific options. Built-in providers may use the connection string and
    /// option list; plugin providers additionally use the plugin name and assembly.
    /// </summary>
    public CustomDatabaseOptions? CustomProviderOptions { get; set; }

    /// <summary>
    /// Gets or Sets the kind of locking behavior jellyfin should perform. Possible options are "NoLock", "Pessimistic", "Optimistic".
    /// Defaults to "NoLock".
    /// </summary>
    public DatabaseLockingBehaviorTypes LockingBehavior { get; set; }
}
