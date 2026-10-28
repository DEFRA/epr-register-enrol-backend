using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>The application's business plan section (legacy <see cref="AccreditationApplicationBusinessPlan"/>), with its status stored as a string.</summary>
public class BusinessPlanSection : BusinessPlan
{
    [BsonRepresentation(BsonType.String)]
    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    /// <summary>What was submitted, snapshotted on each submit and resubmit.</summary>
    public List<BusinessPlanSectionSnapshot> Versions { get; set; } = [];
}
