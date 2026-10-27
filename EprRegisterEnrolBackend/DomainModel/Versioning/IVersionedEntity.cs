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

public static class VersionSentinel
{
    /// <summary>
    /// The <c>validTo</c> of the current version: 9999-12-31T23:59:59.999Z, the largest BSON date. The driver
    /// reads that value back as <see cref="DateTime.MaxValue"/>, so this is the same value, not an approximation.
    /// </summary>
    public static readonly DateTime ValidTo = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
}
