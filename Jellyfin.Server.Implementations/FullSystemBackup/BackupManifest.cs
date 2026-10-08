using System;

namespace Jellyfin.Server.Implementations.FullSystemBackup;

/// <summary>
/// Manifest type for backups internal structure.
/// </summary>
internal class BackupManifest
{
    public required Version ServerVersion { get; set; }

    public required Version BackupEngineVersion { get; set; }

    public required DateTimeOffset DateCreated { get; set; }

    /// <summary>
    /// Gets or sets the stable identity of the provider that exported the database.
    /// </summary>
    /// <remarks>
    /// This is null for version 0.2 archives created before database provider identity was recorded. The archived
    /// database configuration must be inspected before restoring such an archive.
    /// </remarks>
    public string? DatabaseProvider { get; set; }

    public required string[] DatabaseTables { get; set; }

    public required BackupOptions Options { get; set; }
}
