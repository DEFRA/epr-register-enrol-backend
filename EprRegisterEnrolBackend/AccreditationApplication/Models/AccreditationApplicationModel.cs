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

    public required int Year { get; set; }

    public string? SiteId { get; set; }

    public required MaterialType MaterialType { get; set; }

    public ApplicationStatus ApplicationStatus { get; set; } = ApplicationStatus.Saved;

    public string? SourceReExAccreditationId { get; set; }

    public int? SourceYear { get; set; }

    public string? ApplicationReference { get; set; }

    public SubmittedByModel? SubmittedBy { get; set; }

    public DateTime? DateSent { get; set; }

    public DateTime DateLastEdited { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public AccreditationApplicationPrns Prns { get; set; } = new();

    public AccreditationApplicationBusinessPlan BusinessPlan { get; set; } = new();

    public AccreditationApplicationSamplingPlan SamplingPlan { get; set; } = new();
}

public class SubmittedByModel
{
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public required string Email { get; set; }
}

public enum MaterialType
{
    Steel,
    Wood,
    Aluminium,
    Fibre,
    Glass,
    Paper,
    Plastic
}

public enum ApplicationStatus
{
    Saved,
    Started,
    Sent,
    Approved,
    Rejected
}

public enum SectionStatus
{
    NotStarted,
    InProgress,
    Completed
}
