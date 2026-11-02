using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// Brings the domain model in line with a migrated legacy application after a write path has changed it, inside
/// the same transaction (design doc §6.2, B8c and B8g).
/// </summary>
public interface IApplicationModelSync
{
    /// <summary>Does nothing for an application that does not exist or has no Application version yet.</summary>
    Task SyncAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    );
}
