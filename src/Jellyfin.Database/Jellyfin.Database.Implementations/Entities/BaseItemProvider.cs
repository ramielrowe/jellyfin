using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Represents a Key-Value relation of an BaseItem's provider.
/// </summary>
public class BaseItemProvider
{
    private string _providerId = null!;

    /// <summary>
    /// Gets or Sets the reference ItemId.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or Sets the reference BaseItem.
    /// </summary>
    public required BaseItemEntity Item { get; set; }

    /// <summary>
    /// Gets or Sets the ProvidersId.
    /// </summary>
    public required string ProviderId
    {
        get => _providerId;
        set
        {
            _providerId = value;
            ProviderIdDigest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        }
    }

    /// <summary>
    /// Gets the fixed-size digest used as the PostgreSQL key for an unbounded provider id.
    /// </summary>
    public byte[] ProviderIdDigest { get; private set; } = null!;

    /// <summary>
    /// Gets or Sets the Providers Value.
    /// </summary>
    public required string ProviderValue { get; set; }
}
