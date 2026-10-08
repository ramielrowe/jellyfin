using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Jellyfin.Database.Implementations.DbConfiguration;

/// <summary>
/// Defines connection and provider-specific options for a database connector.
/// </summary>
public class CustomDatabaseOptions
{
    /// <summary>
    /// Gets or sets the Plugin name to search for database providers.
    /// </summary>
    public required string PluginName { get; set; }

    /// <summary>
    /// Gets or sets the plugin assembly to search for providers.
    /// </summary>
    public required string PluginAssembly { get; set; }

    /// <summary>
    /// Gets or sets the connection string for the database provider.
    /// This value can contain credentials and must not be written to logs or exception messages.
    /// </summary>
    public required string ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the list of extra options for the custom provider.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only
    public Collection<CustomDatabaseOption> Options { get; set; } = [];
#pragma warning restore CA2227 // Collection properties should be read only
}
