using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>Who changed an entity, when and why: stored on every version as <c>lastChange</c>.</summary>
public class ChangeInfo
{
    [BsonRepresentation(BsonType.String)]
    public required ChangeReason Reason { get; set; }

    public string? ChangedBy { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>The application that caused the change, as its stable <c>entityId</c>.</summary>
    public ObjectId? ApplicationId { get; set; }

    /// <summary>Small, non-personal context such as the <c>orsId</c> or <c>siteNumber</c> concerned.</summary>
    public Dictionary<string, string>? Details { get; set; }

    /// <summary>A change record with the <c>orsId</c> and <c>siteNumber</c> concerned as its details, when given.</summary>
    public static ChangeInfo Of(
        ChangeReason reason,
        ObjectId? applicationId = null,
        string? orsId = null,
        string? siteNumber = null
    )
    {
        var details = new Dictionary<string, string>();
        if (orsId is not null)
            details["orsId"] = orsId;
        if (siteNumber is not null)
            details["siteNumber"] = siteNumber;
        return new ChangeInfo
        {
            Reason = reason,
            ApplicationId = applicationId,
            Details = details.Count > 0 ? details : null,
        };
    }
}
