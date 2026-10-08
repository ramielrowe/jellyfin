using System;

namespace Jellyfin.Database.Implementations;

/// <summary>
/// Represents an actionable, credential-safe database failure encountered during server startup.
/// </summary>
public sealed class DatabaseProviderStartupException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseProviderStartupException"/> class.
    /// </summary>
    /// <param name="providerKey">The stable database provider key.</param>
    /// <param name="category">The stable error category.</param>
    /// <param name="message">The credential-safe operator guidance.</param>
    public DatabaseProviderStartupException(
        string providerKey,
        DatabaseProviderStartupErrorCategory category,
        string message)
        : base(message)
    {
        ProviderKey = providerKey;
        Category = category;
    }

    /// <summary>
    /// Gets the stable database provider key.
    /// </summary>
    public string ProviderKey { get; }

    /// <summary>
    /// Gets the stable error category.
    /// </summary>
    public DatabaseProviderStartupErrorCategory Category { get; }
}
