using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// One version of an Application, in <c>applicationVersions</c>. It is a common envelope (ids, type, lifecycle) with
/// a <see cref="Details"/> whose shape depends on <see cref="ApplicationType"/> (option C1,
/// register-enrol-application-types.md). A field belongs on the envelope only if every type has it and it is queried.
/// For an accreditation application the <c>entityId</c> is the legacy <c>accreditationApplications._id</c>, so the
/// public <c>{applicationId}</c> never changes (identity rule, design doc §3.1). The legacy collection stays the source
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

    /// <summary>
    /// Which type this application is, taken from <see cref="Details"/> so the two can never disagree. Stored for
    /// queries and indexes; it is the same string as <c>details._t</c>. Never read back: <see cref="Details"/> decides.
    /// </summary>
    [BsonElement("applicationType")]
    [BsonRepresentation(BsonType.String)]
    public ApplicationType ApplicationType => Details.ApplicationType;

    [BsonRepresentation(BsonType.String)]
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Saved;

    public string? ApplicationReference { get; set; }
    public string? PaymentReference { get; set; }

    /// <summary>
    /// Shared by the applications submitted together, such as one batch of in-year ORS changes. A single application
    /// is a submission of one. Null until the first type that submits in batches writes it.
    /// </summary>
    public string? SubmissionReference { get; set; }

    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid? CaseManagementWorkItemId { get; set; }

    public DateTime? CaseManagementStatusUpdatedAt { get; set; }

    /// <summary>
    /// Held while the application is open, so a second open application with the same key is refused by the
    /// <c>exclusivityKey_current_u</c> index; see <see cref="ExclusivityKeys"/>. Nothing writes it yet, and known
    /// duplicate legacy applications hold none until they are repaired (design doc §6.3).
    /// </summary>
    public string? ExclusivityKey { get; set; }
    public Submitter? SubmittedBy { get; set; }
    public string? WithdrawalReason { get; set; }
    public DateTime? DateSent { get; set; }
    public DateTime DateLastEdited { get; set; }
    public ApplicationQuery? Query { get; set; }

    /// <summary>The fields that belong to this type only.</summary>
    public required ApplicationDetails Details { get; set; }
}
