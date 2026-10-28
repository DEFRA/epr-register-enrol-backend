using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// Base for the versioned entities. The element names are explicit so the stored shape of the version fields
/// does not depend on a naming convention being registered. Every field except <see cref="EntityId"/> describes
/// the version, not the entity, so it is excluded from change detection.
/// </summary>
public abstract class VersionedEntityBase : IVersionedEntity
{
    [BsonId(IdGenerator = typeof(ObjectIdGenerator))]
    [ExcludedFromChangeDetection]
    public ObjectId Id { get; set; }

    [BsonElement("entityId")]
    public ObjectId EntityId { get; set; }

    [BsonElement("version")]
    [ExcludedFromChangeDetection]
    public long Version { get; set; }

    [BsonElement("validFrom")]
    [ExcludedFromChangeDetection]
    public DateTime ValidFrom { get; set; }

    [BsonElement("validTo")]
    [ExcludedFromChangeDetection]
    public DateTime ValidTo { get; set; } = VersionSentinel.ValidTo;

    [BsonElement("isCurrent")]
    [ExcludedFromChangeDetection]
    public bool IsCurrent { get; set; }

    [BsonElement("lastChange")]
    [ExcludedFromChangeDetection]
    public ChangeInfo LastChange { get; set; } = new() { Reason = ChangeReason.Migration };
}
