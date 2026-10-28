using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>The application's PRNs section (legacy <see cref="AccreditationApplicationPrns"/>), with its enums stored as strings.</summary>
public class PrnsSection
{
    [BsonSerializer(typeof(TonnageBandSerializer))]
    public TonnageBand? PlannedTonnageBand { get; set; }

    public List<PrnsAuthoriser> Authorisers { get; set; } = [];

    [BsonRepresentation(BsonType.String)]
    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    /// <summary>What was submitted, snapshotted on each submit and resubmit.</summary>
    public List<PrnsSectionSnapshot> Versions { get; set; } = [];
}
