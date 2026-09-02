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

    // RA-503: ReEx's numeric organisation number (e.g. 500500) - the value an operator or
    // regulator should actually see. Distinct from OrganisationId above, which is ReEx's own
    // internal ObjectId and must never be surfaced to an operator or regulator (see
    // OrganisationDto.cs). Resolved once and persisted: Submit resolves it fresh on first
    // submission; GetById backfills it (and stores the result) for any application read
    // before that resolution has happened, so later reads skip the ReEx round trip entirely.
    public int? OrgId { get; set; }

    public string? OrganisationName { get; set; }

    public required int Year { get; set; }

    public string? RegistrationId { get; set; }

    public bool IsExporter { get; set; }

    // RA-526: derived at Seed time from the source registration's own SubmittedToRegulator
    // (never the organisation's, and never from postcode - see RegulatorNationMapper).
    // Nullable so applications seeded before this field existed still deserialize.
    [BsonRepresentation(BsonType.String)]
    public Nation? Nation { get; set; }

    public string? SiteAddress { get; set; }

    public string? CompanyRegisterAddressPostcode { get; set; }

    // RA-424: full formatted UK registered address, used by the frontend to show the exporter's
    // registered office in place of the (non-existent) uk based site address on the accreditation
    // application header/landing page.
    public string? CompanyRegisteredAddress { get; set; }

    // RA-526: true when CompanyRegisteredAddress came from companyDetails.registeredAddress
    // rather than the companyDetails.address fallback.
    public bool IsUkRegisteredAddress { get; set; }

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

    // RA-503: the operator's real, nation-specific bank payment reference (buildPaymentReference
    // in epr-register-enrol-frontend, e.g. PR/PK/REP/500500) - the exact string shown to the
    // operator on their submit-confirmation and view-payment-details pages, captured from
    // SubmitRequest at Submit time and forwarded to management-be so the regulator's duly-making
    // page shows the same reference the operator was actually told to quote.
    public string? PaymentReference { get; set; }

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

    // RA-480: the original registration submitter's contact details, sourced from ReEx at Seed
    // time. Distinct from SubmittedBy (captured at Case Management service submit time, a
    // different person) and AccreditationApplicationQuery.QuerySubmitterContactDetails (the
    // query/withdrawal responder).
    public SubmitterContactDetailsModel? SubmitterContactDetails { get; set; }

    public string? WithdrawalReason { get; set; }

    public DateTime? DateSent { get; set; }

    // Internal ordering guard for RA-368's status-push endpoint — not displayed. A push is
    // applied only if its OccurredAt is strictly after this value, so pushes arriving
    // out of order (e.g. a delayed retry) can't regress the application's status.
    public DateTime? CaseManagementStatusUpdatedAt { get; set; }

    public DateTime DateLastEdited { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // RA-516: optimistic concurrency token, checked and incremented by
    // AccreditationApplicationPersistence.ReplaceIfMatchAsync so two concurrent read-modify-write
    // updates can't silently overwrite each other - the second writer's filter no longer matches
    // once the first writer's update has moved this on, and ReplaceOneAsync's ModifiedCount==0
    // already means "did not persist" to every caller.
    public long Version { get; set; }

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
    // the Registration & Accreditation service has no due date of its own, the Case Management
    // service's work item is the single source of truth. Never persisted (same rationale as
    // NotificationStatus above); null when there is no linked work item yet.
    [BsonIgnore]
    public DateTime? DueDate { get; set; }
}

public class SubmittedByModel
{
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public string? Email { get; set; }
}

public class SubmitterContactDetailsModel
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? JobTitle { get; set; }
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
