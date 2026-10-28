using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

public class PrnsSectionSnapshot
{
    [BsonSerializer(typeof(TonnageBandSerializer))]
    public TonnageBand? PlannedTonnageBand { get; set; }

    public List<PrnsAuthoriser> Authorisers { get; set; } = [];
    public DateTime VersionedAt { get; set; }
}
