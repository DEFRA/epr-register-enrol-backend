using System.Globalization;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>A site's approval date as ReEx writes it: a raw date string on the legacy application.</summary>
public static class SiteApprovalDate
{
    /// <summary>The date in UTC, or null when there is none or it cannot be read.</summary>
    public static DateTime? Parse(string? value) =>
        DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date
        )
            ? date
            : null;

    /// <summary>The one form the read model serves it in, to the millisecond Mongo keeps.</summary>
    public static string? Format(DateTime? date) =>
        date is { } value
            ? new BsonDateTime(value).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : null;
}
