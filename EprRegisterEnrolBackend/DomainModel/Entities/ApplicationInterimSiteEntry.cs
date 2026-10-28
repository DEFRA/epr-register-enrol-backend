using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>An interim site selection. The interim site has no id, so it is referred to by <see cref="SiteNumber"/> within its ORS.</summary>
public class ApplicationInterimSiteEntry
{
    public int LegacySiteId { get; set; }
    public required string SiteNumber { get; set; }
    public bool IsNewSite { get; set; }
    public BsonDocument? ProposedChanges { get; set; }
}
