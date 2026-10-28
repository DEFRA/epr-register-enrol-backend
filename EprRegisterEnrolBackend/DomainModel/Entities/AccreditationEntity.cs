using EprRegisterEnrolBackend.DomainModel.Entities.Sections;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// One version of an Accreditation, in <c>accreditationVersions</c>. Created when an application is approved
/// here; ReEx reads it through the v2 API to decide whether PRNs may be issued. Status and tonnage band are
/// stored and recorded-time only, so their history is the version history (Q21, A19).
/// </summary>
public class AccreditationEntity : VersionedEntityBase
{
    /// <summary>The Registration's <c>entityId</c>: the only link upward.</summary>
    public ObjectId RegistrationId { get; set; }

    /// <summary>The year the accreditation applies to, not the year it was created or approved.</summary>
    public int Year { get; set; }

    /// <summary>Immutable once approved. Unique across organisations among current versions.</summary>
    public string? AccreditationNumber { get; set; }

    /// <summary>The approving Application's <c>entityId</c>.</summary>
    public ObjectId? ApplicationId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public AccreditationStatus Status { get; set; } = AccreditationStatus.Approved;

    /// <summary>
    /// When the regulator approved it. A business date, kept because the first version's <c>validFrom</c> is the
    /// time the approval was recorded here, which can be later (§4.3).
    /// </summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>Business validity, not history.</summary>
    public DateTime? PeriodStart { get; set; }

    public DateTime? PeriodEnd { get; set; }

    public List<Signatory> Signatories { get; set; } = [];

    /// <summary>Owned here, not exposed by the v2 API.</summary>
    public BusinessPlan? BusinessPlan { get; set; }

    /// <summary>Kept, not exposed.</summary>
    public List<ApplicationFile> SamplingPlanFiles { get; set; } = [];

    /// <summary>Kept, not exposed (Q11). Its source is still to be confirmed, so it holds the values as given.</summary>
    public List<string> RecyclingProcess { get; set; } = [];

    public List<AccreditationOverseasSiteEntry> AccreditationOverseasSites { get; set; } = [];

    [BsonSerializer(typeof(TonnageBandSerializer))]
    public TonnageBand? TonnageBand { get; set; }
}
