using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Cut;

/// <summary>
/// RA-706 decision 6 - before the transaction commits, every staged application is brought into the model by the dual-write migrator and sync (reading the staged copy through the model-backed persistence), and the copies are deleted in the same transaction; returns the migration's conflicts for the caller to log.
/// </summary>
public sealed class CutFlush(
    IApplicationGroupMigrator migrator,
    IApplicationModelSync sync,
    CutStagingPersistence staging
)
{
    /// <summary>
    /// called at the start of each transaction attempt; a retried attempt reuses the session and must not inherit what the rolled-back attempt staged.
    /// </summary>
    public void Begin(IClientSessionHandle session) => staging.Forget(session);

    public async Task<IReadOnlyList<MigrationConflict>> FlushAsync(
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var flushed = new HashSet<ObjectId>();
        var conflicts = new List<MigrationConflict>();

        try
        {
            while (true)
            {
                var currentStaged = staging.StagedIds(session);
                var remaining = currentStaged.Where(id => !flushed.Contains(id)).ToList();

                if (remaining.Count == 0)
                {
                    break;
                }

                foreach (var id in remaining)
                {
                    var migration = await migrator.EnsureMigratedAsync(id, session, cancellationToken);
                    conflicts.AddRange(migration.Conflicts);
                    await sync.SyncAsync(id, session, cancellationToken);
                    flushed.Add(id);
                }
            }

            await staging.DeleteStagedAsync(flushed, session, cancellationToken);
        }
        finally
        {
            staging.Forget(session);
        }

        return conflicts;
    }
}
