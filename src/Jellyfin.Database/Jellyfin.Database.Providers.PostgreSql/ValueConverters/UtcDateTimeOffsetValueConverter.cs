using System;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Jellyfin.Database.Providers.PostgreSql.ValueConverters;

/// <summary>
/// Normalizes <see cref="DateTimeOffset"/> values to the zero offset required by Npgsql's
/// <c>timestamp with time zone</c> mapping.
/// </summary>
internal sealed class UtcDateTimeOffsetValueConverter : ValueConverter<DateTimeOffset, DateTimeOffset>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UtcDateTimeOffsetValueConverter"/> class.
    /// </summary>
    public UtcDateTimeOffsetValueConverter()
        : base(
            value => value.ToUniversalTime(),
            value => value.ToUniversalTime())
    {
    }
}
