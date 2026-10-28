using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities.Sections;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// One version of an Application, in <c>applicationVersions</c>: the operator's proposal plus case-processing
/// state. Its <c>entityId</c> is the legacy <c>accreditationApplications._id</c>, so the public
/// <c>{applicationId}</c> never changes (identity rule, design doc §3.1). The legacy collection stays the source
/// of truth for writes until the cut (Q33).
/// </summary>
public class ApplicationEntity : VersionedEntityBase
{
    /// <summary>The Registration's <c>entityId</c>. Null until the migration or dual-write has linked it.</summary>
    public ObjectId? RegistrationId { get; set; }

    /// <summary>
    /// ReEx's ids, copied from the legacy application. They are what the migration and dual-write use to find or
    /// create the Organisation and Registration, so they are needed before <see cref="RegistrationId"/> is set.
    /// </summary>
    public required string ReExOrganisationId { get; set; }

    public string? ReExRegistrationId { get; set; }

    /// <summary>The accreditation year applied for.</summary>
    public int Year { get; set; }

    /// <summary>The Accreditation (prior year) this application was seeded from. Null for migrated 2027 applications (D10).</summary>
    public ObjectId? SourceAccreditationId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Saved;

    public string? ApplicationReference { get; set; }
    public string? PaymentReference { get; set; }

    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? CaseManagementWorkItemId { get; set; }

    public DateTime? CaseManagementStatusUpdatedAt { get; set; }
    public SubmittedByModel? SubmittedBy { get; set; }
    public string? WithdrawalReason { get; set; }
    public DateTime? DateSent { get; set; }
    public DateTime DateLastEdited { get; set; }
    public AccreditationApplicationQuery? Query { get; set; }

    public PrnsSection Prns { get; set; } = new();
    public BusinessPlanSection BusinessPlan { get; set; } = new();
    public SamplingPlanSection SamplingPlan { get; set; } = new();
    public BesEvidenceSection? BesEvidence { get; set; }

    /// <summary>The overseas sites section's status and submission history. The sites are <see cref="ApplicationOverseasSites"/>.</summary>
    public OverseasSitesSection? OverseasSitesSection { get; set; }

    /// <summary>One entry per overseas site this application touches.</summary>
    public List<ApplicationOverseasSiteEntry> ApplicationOverseasSites { get; set; } = [];

    public string? ReservedAccreditationNumber { get; set; }

    /// <summary>Set when the application is approved.</summary>
    public ObjectId? AccreditationId { get; set; }
}
