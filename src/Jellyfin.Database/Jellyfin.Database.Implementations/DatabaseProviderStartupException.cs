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
    /// <param name="innerException">The provider exception that caused the startup failure, when available.</param>
    public DatabaseProviderStartupException(
        string providerKey,
        DatabaseProviderStartupErrorCategory category,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
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
