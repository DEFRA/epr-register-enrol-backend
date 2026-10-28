using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>An interim site, embedded in its ORS. <see cref="SiteNumber"/> is its stable identity within the registration.</summary>
public class InterimSiteEntry
{
    /// <summary>Three digits from the registration's own pool, separate from ORS ids.</summary>
    public required string SiteNumber { get; set; }

    public List<string> PreviousSiteNumbers { get; set; } = [];
    public string? SiteName { get; set; }
    public PostalAddress? Address { get; set; }
    public ContactPerson? Contact { get; set; }
    public List<string> OperationCodes { get; set; } = [];

    [BsonRepresentation(BsonType.String)]
    public InterimSiteStatus Status { get; set; } = InterimSiteStatus.Proposed;

    public ObjectId? ProposedByApplicationId { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
}
