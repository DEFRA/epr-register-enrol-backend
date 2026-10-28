using EprRegisterEnrolBackend.DomainModel.Entities.Sections;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>An accredited overseas site (was <c>sites[]</c>). Its interim sites are those of its ORS, with no per-accreditation membership.</summary>
public class AccreditationOverseasSiteEntry
{
    public ObjectId OverseasSiteId { get; set; }
    public string? OrsId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public AccreditedSiteStatus Status { get; set; } = AccreditedSiteStatus.Approved;

    public List<BesEvidenceFile> BesEvidence { get; set; } = [];
}
