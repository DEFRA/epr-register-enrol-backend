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
}
