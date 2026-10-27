using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// Base for the versioned entities. The element names are explicit because the repository's filters
/// and indexes address them by name and must not depend on a naming convention being registered.
/// </summary>
public abstract class VersionedEntityBase : IVersionedEntity
{
    [BsonId(IdGenerator = typeof(ObjectIdGenerator))]
    public ObjectId Id { get; set; }

    [BsonElement("entityId")]
    public ObjectId EntityId { get; set; }

    [BsonElement("version")]
    public long Version { get; set; }

    [BsonElement("validFrom")]
    public DateTime ValidFrom { get; set; }

    [BsonElement("validTo")]
    public DateTime ValidTo { get; set; } = VersionSentinel.ValidTo;

    [BsonElement("isCurrent")]
    public bool IsCurrent { get; set; }

    [BsonElement("lastChange")]
    public ChangeInfo LastChange { get; set; } = new() { Reason = ChangeReason.Migration };
}
