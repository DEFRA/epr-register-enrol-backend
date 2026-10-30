using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// The per-application migration routine shared by the dual-write path (phase 3, §6.2 ensure-migrated) and the
/// migration service (phase 4, §6.1).
/// </summary>
public interface IApplicationGroupMigrator
{
    /// <summary>
    /// Migrates the registration group of <paramref name="applicationId"/> inside <paramref name="session"/>'s
    /// transaction, unless every application in it already has a current version. Idempotent: what already exists
    /// is reused, and only what is missing is written.
    /// </summary>
    /// <exception cref="ApplicationGroupTooLargeException">The group is too big for one transaction.</exception>
    Task<GroupMigrationResult> EnsureMigratedAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    );
}
