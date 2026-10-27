using EprRegisterEnrolBackend.AccreditationApplication.Models;
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

    public AccreditationApplicationPrns Prns { get; set; } = new();
    public AccreditationApplicationBusinessPlan BusinessPlan { get; set; } = new();
    public AccreditationApplicationSamplingPlan SamplingPlan { get; set; } = new();
    public AccreditationApplicationBesEvidence? BesEvidence { get; set; }

    /// <summary>The overseas sites section's status and submission history (a different thing from <see cref="ApplicationOverseasSites"/>).</summary>
    public AccreditationApplicationOverseasSites? OverseasSites { get; set; }

    public List<ApplicationOverseasSiteEntry> ApplicationOverseasSites { get; set; } = [];

    public string? ReservedAccreditationNumber { get; set; }

    /// <summary>Set when the application is approved.</summary>
    public ObjectId? AccreditationId { get; set; }
}

/// <summary>One entry per overseas site an application touches (was <c>siteSelections[]</c>).</summary>
public class ApplicationOverseasSiteEntry
{
    /// <summary>The existing per-application int, kept so <c>{siteId}</c> routes still resolve.</summary>
    public int SiteId { get; set; }

    public ObjectId? OverseasSiteId { get; set; }
    public bool Selected { get; set; } = true;
    public bool IsNewSite { get; set; }
    public bool RegisteredNowAccredited { get; set; }

    /// <summary>An overlay of the ORS fields edited in this application, applied to the ORS only on approval.</summary>
    public BsonDocument? ProposedChanges { get; set; }

    public List<BsonDocument> PreviousSites { get; set; } = [];
    public BesEvidenceModel? BesEvidence { get; set; }
    public List<ApplicationInterimSiteEntry> InterimSites { get; set; } = [];
}

/// <summary>An interim site selection. The interim site has no id, so it is referred to by <see cref="SiteNumber"/> within its ORS.</summary>
public class ApplicationInterimSiteEntry
{
    public int LegacySiteId { get; set; }
    public required string SiteNumber { get; set; }
    public bool IsNewSite { get; set; }
    public BsonDocument? ProposedChanges { get; set; }
}
