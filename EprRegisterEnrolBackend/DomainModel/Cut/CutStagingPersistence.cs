using System.Runtime.CompilerServices;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Cut;

/// <summary>
/// RA-706 decision 6 - the legacy persistence over transaction-scoped copies; a write's version check, guard filters and update operators run here exactly as on the legacy collection; the flush writes the model from the copies and deletes them in the same transaction, so nothing is left outside a transaction.
/// </summary>
public sealed class CutStagingPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
)
    : AccreditationApplicationPersistence(
        connectionFactory,
        loggerFactory,
        DomainModelCollections.CutStagingApplications
    )
{
    private readonly ConditionalWeakTable<IClientSessionHandle, List<ObjectId>> _staged = new();

    /// <summary>
    /// Inserts a copy of the application into the staging collection and tracks it for the session.
    /// </summary>
    public async Task StageAsync(
        AccreditationApplicationModel copy,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        await Collection.InsertOneAsync(session, copy, cancellationToken: cancellationToken);
        Track(session, copy.Id!.Value);
    }

    /// <summary>
    /// Deletes the specified staged application copies in the same transaction.
    /// </summary>
    public async Task DeleteStagedAsync(IEnumerable<ObjectId> applicationIds, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var ids = applicationIds.ToList();
        if (ids.Count == 0) return;
        await Collection.DeleteManyAsync(session, Builders<AccreditationApplicationModel>.Filter.In(a => a.Id, ids.Select(id => (ObjectId?)id)), cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Adds an application ID to the session's staged list if it is not already present.
    /// </summary>
    public void Track(IClientSessionHandle session, ObjectId applicationId)
    {
        var list = _staged.GetValue(session, _ => []);
        lock (list)
        {
            if (!list.Contains(applicationId))
            {
                list.Add(applicationId);
            }
        }
    }

    /// <summary>
    /// Returns a snapshot of the application IDs staged for the given session.
    /// </summary>
    public IReadOnlyList<ObjectId> StagedIds(IClientSessionHandle session)
    {
        if (!_staged.TryGetValue(session, out var list))
        {
            return [];
        }

        lock (list)
        {
            return list.ToList();
        }
    }

    /// <summary>
    /// Removes the tracking data for the given session.
    /// </summary>
    public void Forget(IClientSessionHandle session)
    {
        _staged.Remove(session);
    }
}
