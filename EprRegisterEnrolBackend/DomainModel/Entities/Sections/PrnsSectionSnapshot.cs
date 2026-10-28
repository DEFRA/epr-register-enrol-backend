using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

public class PrnsSectionSnapshot
{
    [BsonSerializer(typeof(TonnageBandSerializer))]
    public TonnageBand? PlannedTonnageBand { get; set; }

    public List<Authoriser> Authorisers { get; set; } = [];
    public DateTime VersionedAt { get; set; }
}
