using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace EprRegisterEnrolBackend.StubPersistence.Models;

public class StubApplicationDocument
{
    [BsonId(IdGenerator = typeof(ObjectIdGenerator))]
    public ObjectId Id { get; set; }

    public required string StubApplicationId { get; set; }

    public required string OrganisationId { get; set; }

    public string? SiteId { get; set; }

    public string MaterialType { get; set; } = string.Empty;

    public int Year { get; set; }

    public BsonDocument Data { get; set; } = new();

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
