using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Versioning;

/// <summary>
/// The version a writer started from is no longer current, or another writer closed it first.
/// The caller should re-read and decide again; nothing was written.
/// </summary>
public class VersionConflictException(ObjectId entityId, long expectedVersion)
    : Exception($"Entity {entityId} is not at version {expectedVersion}.")
{
    public ObjectId EntityId { get; } = entityId;

    public long ExpectedVersion { get; } = expectedVersion;
}
