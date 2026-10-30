using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>The <c>migrationConflicts</c> collection: what the migration could not settle by rule (§6.1).</summary>
public class MigrationConflictPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
)
    : DomainModelPersistence<MigrationConflict>(
        connectionFactory,
        DomainModelCollections.MigrationConflicts,
        loggerFactory
    )
{
    public async Task InsertManyAsync(
        IReadOnlyCollection<MigrationConflict> conflicts,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        if (conflicts.Count > 0)
        {
            await Collection.InsertManyAsync(
                session,
                conflicts,
                cancellationToken: cancellationToken
            );
        }
    }

    /// <summary>Whether a conflict of <paramref name="kind"/> is already recorded for the application.</summary>
    public Task<bool> ExistsAsync(
        MigrationConflictKind kind,
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) =>
        Collection
            .Find(
                session,
                Builders<MigrationConflict>.Filter.Eq(c => c.Kind, kind)
                    & Builders<MigrationConflict>.Filter.AnyEq(c => c.ApplicationIds, applicationId)
            )
            .AnyAsync(cancellationToken);

    protected override List<CreateIndexModel<MigrationConflict>> DefineIndexes(
        IndexKeysDefinitionBuilder<MigrationConflict> builder
    ) =>
        [
            new(
                builder.Ascending(x => x.ReExRegistrationId),
                new CreateIndexOptions { Name = "reExRegistrationId" }
            ),
        ];
}
