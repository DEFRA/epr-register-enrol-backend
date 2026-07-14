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

    public SubmittedByModel? SubmittedBy { get; set; }

    public DateTime? DateSent { get; set; }

    public DateTime DateLastEdited { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AccreditationApplicationPrns Prns { get; set; } = new();

    public AccreditationApplicationBusinessPlan BusinessPlan { get; set; } = new();

    public AccreditationApplicationSamplingPlan SamplingPlan { get; set; } = new();

    public AccreditationApplicationOverseasSites? OverseasSites { get; set; }

    public AccreditationApplicationBesEvidence? BesEvidence { get; set; }

    // RA102-j7s: live-derived on GetById from the linked ManagementBe work item's audit log —
    // never persisted, so BsonIgnore keeps a transient read-time value out of Mongo writes.
    [BsonIgnore]
    public string? NotificationStatus { get; set; }
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
    Sent,
    Approved,
    Rejected,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SectionStatus
{
    NotStarted,
    InProgress,
    Completed,
}
