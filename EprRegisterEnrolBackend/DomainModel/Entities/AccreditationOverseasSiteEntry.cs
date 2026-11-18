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

    /// <summary>
    /// When the site is accredited on this Accreditation from, when that is not when the Accreditation was approved:
    /// a granted in-year change request's submission date (RA-495). The legacy application holds it as the site's
    /// <c>validFrom</c>, so the read model serves it as that for this Accreditation's application only (3bb.43,
    /// 3bb.44). Null for a site accredited with the Accreditation, which serves its ORS's ReEx date if it has one.
    /// Not the ORS's <c>firstApprovedAt</c> (first approved ever) nor the versioning <c>validFrom</c>.
    /// </summary>
    public DateTime? AccreditedFrom { get; set; }
}
