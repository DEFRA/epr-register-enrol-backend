using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// The <c>domainModelMigrations</c> collection: the migration's lease and progress (§6.1). Bookkeeping for the run, not
/// part of the model, so it is not one of the collections the schema check verifies.
/// </summary>
public class DomainModelMigrationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
)
    : MongoService<DomainModelMigrationState>(
        connectionFactory,
        DomainModelCollections.DomainModelMigrations,
        loggerFactory
    )
{
    private static FilterDefinitionBuilder<DomainModelMigrationState> Filter =>
        Builders<DomainModelMigrationState>.Filter;

    private static FilterDefinition<DomainModelMigrationState> ThisMigration =>
        Filter.Eq(s => s.Id, DomainModelMigrationState.ApplicationsMigration);

    /// <summary>Takes the lease if nobody holds it or it has expired. Null when another instance holds it.</summary>
    public async Task<DomainModelMigrationState?> TryAcquireLeaseAsync(
        string owner,
        DateTime now,
        DateTime expiresAt,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await Collection.FindOneAndUpdateAsync(
                ThisMigration
                    & Filter.Or(
                        Filter.Eq(s => s.LeaseOwner, null),
                        Filter.Lte(s => s.LeaseExpiresAt, now)
                    ),
                Builders<DomainModelMigrationState>
                    .Update.Set(s => s.LeaseOwner, owner)
                    .Set(s => s.LeaseExpiresAt, expiresAt),
                new FindOneAndUpdateOptions<DomainModelMigrationState>
                {
                    IsUpsert = true,
                    ReturnDocument = ReturnDocument.After,
                },
                cancellationToken
            );
        }
        // The document exists and its lease is held, so the upsert tried to insert a second one.
        catch (MongoException ex) when (MongoErrors.IsDuplicateKey(ex))
        {
            return null;
        }
    }

    /// <summary>Saves progress and renews the lease. False when this instance no longer holds it.</summary>
    public async Task<bool> SaveProgressAsync(
        string owner,
        DateTime expiresAt,
        DomainModelMigrationState progress,
        CancellationToken cancellationToken
    )
    {
        var result = await Collection.UpdateOneAsync(
            ThisMigration & Filter.Eq(s => s.LeaseOwner, owner),
            Builders<DomainModelMigrationState>
                .Update.Set(s => s.LeaseExpiresAt, expiresAt)
                .Set(s => s.PassStartedAt, progress.PassStartedAt)
                .Set(s => s.PassCompletedAt, progress.PassCompletedAt)
                .Set(s => s.Examined, progress.Examined)
                .Set(s => s.AlreadyMigrated, progress.AlreadyMigrated)
                .Set(s => s.Migrated, progress.Migrated)
                .Set(s => s.Failures, progress.Failures),
            cancellationToken: cancellationToken
        );
        return result.MatchedCount == 1;
    }

    public Task ReleaseLeaseAsync(string owner, CancellationToken cancellationToken) =>
        Collection.UpdateOneAsync(
            ThisMigration & Filter.Eq(s => s.LeaseOwner, owner),
            Builders<DomainModelMigrationState>
                .Update.Set(s => s.LeaseOwner, null)
                .Set(s => s.LeaseExpiresAt, null),
            cancellationToken: cancellationToken
        );

    protected override List<CreateIndexModel<DomainModelMigrationState>> DefineIndexes(
        IndexKeysDefinitionBuilder<DomainModelMigrationState> builder
    ) => [];
}
