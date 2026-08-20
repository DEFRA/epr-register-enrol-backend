using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationModel
{
    [BsonId(IdGenerator = typeof(ObjectIdGenerator))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public ObjectId? Id { get; set; }

    [BsonIgnore]
    public string? ApplicationId => Id?.ToString();

    public required string OrganisationId { get; set; }

    public string? OrganisationName { get; set; }

    public required int Year { get; set; }

    public string? RegistrationId { get; set; }

    public bool IsExporter { get; set; }

    public string? SiteAddress { get; set; }

    public string? CompanyRegisterAddressPostcode { get; set; }

    // RA-424: full formatted UK registered address, used by the frontend to show the exporter's
    // registered office in place of the (non-existent) overseas site address on the accreditation
    // application header/landing page.
    public string? CompanyRegisteredAddress { get; set; }

    public string? CompaniesHouseNumber { get; set; }

    public List<string> PermitNumbers { get; set; } = [];

    public string? WasteProcessingType { get; set; }

    [BsonRepresentation(BsonType.String)]
    public required MaterialType MaterialType { get; set; }

    [BsonRepresentation(BsonType.String)]
    public GlassRecyclingProcess? GlassRecyclingProcess { get; set; }

    [BsonRepresentation(BsonType.String)]
    public ApplicationStatus ApplicationStatus { get; set; } = ApplicationStatus.Saved;

    public string? SourceReExAccreditationId { get; set; }

    public int? SourceYear { get; set; }

    public string? ApplicationReference { get; set; }

    public string? CaseManagementReference { get; set; }

    // Explicit representation required: this driver version defaults an unannotated Guid to
    // GuidRepresentation.Unspecified, which throws BsonSerializationException on write rather
    // than silently picking a default.
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? CaseManagementWorkItemId { get; set; }

    public string? RegistrationReference { get; set; }

    // RA-448: regenerated registration numbers only - RegistrationReference may
    // already be populated at Seed time from ReEx's own RegistrationNumber (see
    // AccreditationApplicationEndpoints.Seed), which is not itself a "regenerate"
    // and does not go through this list.
    public List<string> PreviousRegistrationNumbers { get; set; } = [];

    // RA-448: no ReEx-sourced equivalent exists for accreditation (unlike
    // RegistrationReference) - always issued by this backend's own endpoint.
    public string? AccreditationReference { get; set; }

    // RA-448: "reapply for accreditation" regenerate - a YY-increment string
    // transform, kept distinct from PreviousRegistrationNumbers since a reader
    // later needs to tell at a glance which history belongs to which number.
    public List<string> PreviousAccreditationNumbers { get; set; } = [];

    public SubmittedByModel? SubmittedBy { get; set; }

    public string? WithdrawalReason { get; set; }

    public DateTime? DateSent { get; set; }

    // Internal ordering guard for RA-368's status-push endpoint — not displayed. A push is
    // applied only if its OccurredAt is strictly after this value, so pushes arriving
    // out of order (e.g. a delayed retry) can't regress the application's status.
    public DateTime? CaseManagementStatusUpdatedAt { get; set; }

    public DateTime DateLastEdited { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AccreditationApplicationPrns Prns { get; set; } = new();

    public AccreditationApplicationBusinessPlan BusinessPlan { get; set; } = new();

    public AccreditationApplicationSamplingPlan SamplingPlan { get; set; } = new();

    public AccreditationApplicationOverseasSites? OverseasSites { get; set; }

    public AccreditationApplicationBesEvidence? BesEvidence { get; set; }

    public AccreditationApplicationQuery? Query { get; set; }

    // RA102-j7s: live-derived on GetById from the linked ManagementBe work item's audit log —
    // never persisted, so BsonIgnore keeps a transient read-time value out of Mongo writes.
    [BsonIgnore]
    public string? NotificationStatus { get; set; }

    // RA-415: live-derived on GetById from the linked ManagementBe work item's SLA due date —
    // OJ has no due date of its own, CM's work item is the single source of truth. Never
    // persisted (same rationale as NotificationStatus above); null when there is no linked
    // work item yet.
    [BsonIgnore]
    public DateTime? DueDate { get; set; }
}

public class SubmittedByModel
{
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public string? Email { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApplicationStatus
{
    Saved,
    Started,
    Submitted,
    DulyMade,
    Queried,
    Updated,
    AwaitingDecision,
    Approved,
    Rejected,
    Withdrawn,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SectionStatus
{
    NotStarted,
    InProgress,
    Completed,
    Submitted,
    Queried,
}
