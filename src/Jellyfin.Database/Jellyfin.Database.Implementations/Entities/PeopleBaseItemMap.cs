using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Mapping table for People to BaseItems.
/// </summary>
public class PeopleBaseItemMap
{
    private string? _role;

    /// <summary>
    /// Gets or Sets the SortOrder.
    /// </summary>
    public int? SortOrder { get; set; }

    /// <summary>
    /// Gets or Sets the ListOrder.
    /// </summary>
    public int? ListOrder { get; set; }

    /// <summary>
    /// Gets or Sets the Role name the associated actor played in the <see cref="BaseItemEntity"/>.
    /// </summary>
    public string? Role
    {
        get => _role;
        set
        {
            _role = value;
            RoleDigest = value is null ? null! : SHA256.HashData(Encoding.UTF8.GetBytes(value));
        }
    }

    /// <summary>
    /// Gets the fixed-size UTF-8 digest used as the PostgreSQL key for an unbounded role.
    /// </summary>
    public byte[] RoleDigest { get; private set; } = null!;

    /// <summary>
    /// Gets or Sets The ItemId.
    /// </summary>
    public required Guid ItemId { get; set; }

    /// <summary>
    /// Gets or Sets Reference Item.
    /// </summary>
    public required BaseItemEntity Item { get; set; }

    /// <summary>
    /// Gets or Sets The PeopleId.
    /// </summary>
    public required Guid PeopleId { get; set; }

    /// <summary>
    /// Gets or Sets Reference People.
    /// </summary>
    public required People People { get; set; }
}
