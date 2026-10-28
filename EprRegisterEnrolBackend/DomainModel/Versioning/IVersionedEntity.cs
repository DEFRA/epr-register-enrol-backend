using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// One version of an entity, stored as its own document in a <c>*Versions</c> collection.
/// <see cref="Id"/> is per version. <see cref="EntityId"/> is the stable identity that every other
/// entity, URL, header and payload refers to; a version <see cref="Id"/> must never leave the service.
/// </summary>
public interface IVersionedEntity
{
    ObjectId Id { get; set; }

    ObjectId EntityId { get; set; }

    long Version { get; set; }

    /// <summary>Start of this version's period, inclusive.</summary>
    DateTime ValidFrom { get; set; }

    /// <summary>End of this version's period, exclusive. <see cref="VersionSentinel.ValidTo"/> while current.</summary>
    DateTime ValidTo { get; set; }

    bool IsCurrent { get; set; }

    ChangeInfo LastChange { get; set; }
}
