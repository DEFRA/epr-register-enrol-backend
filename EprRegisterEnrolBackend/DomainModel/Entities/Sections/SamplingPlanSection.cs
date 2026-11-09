using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>The application's sampling plan section (legacy <see cref="AccreditationApplicationSamplingPlan"/>), with its enums stored as strings.</summary>
public class SamplingPlanSection
{
    public List<ApplicationFile> Files { get; set; } = [];

    [BsonRepresentation(BsonType.String)]
    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    /// <summary>What was submitted, snapshotted on each submit and resubmit.</summary>
    public List<SamplingPlanSectionSnapshot> Versions { get; set; } = [];
}
