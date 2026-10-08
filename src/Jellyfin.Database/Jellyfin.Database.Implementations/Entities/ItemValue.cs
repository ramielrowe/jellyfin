using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Represents an ItemValue for a BaseItem.
/// </summary>
public class ItemValue
{
    private string _value = null!;

    /// <summary>
    /// Gets or Sets the ItemValueId.
    /// </summary>
    public required Guid ItemValueId { get; set; }

    /// <summary>
    /// Gets or Sets the Type.
    /// </summary>
    public required ItemValueType Type { get; set; }

    /// <summary>
    /// Gets or Sets the Value.
    /// </summary>
    public required string Value
    {
        get => _value;
        set
        {
            _value = value;
            ValueDigest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        }
    }

    /// <summary>
    /// Gets the fixed-size UTF-8 digest used for PostgreSQL uniqueness enforcement.
    /// </summary>
    public byte[] ValueDigest { get; private set; } = null!;

    /// <summary>
    /// Gets or Sets the sanitized Value.
    /// </summary>
    public required string CleanValue { get; set; }

    /// <summary>
    /// Gets or Sets all associated BaseItems.
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read only
    public ICollection<ItemValueMap>? BaseItemsMap { get; set; }
#pragma warning restore CA2227 // Collection properties should be read only
}
