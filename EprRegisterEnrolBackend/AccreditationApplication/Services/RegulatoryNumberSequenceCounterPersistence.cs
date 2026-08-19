using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class RegulatoryNumberSequenceCounterPersistence
    : MongoService<RegulatoryNumberSequenceCounter>,
        IRegulatoryNumberSequenceCounterPersistence
{
    public RegulatoryNumberSequenceCounterPersistence(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, "regulatoryNumberSequences", loggerFactory)
    {
        // RA-448: MongoService<T>.EnsureIndexes never actually calls
        // Collection.Indexes.CreateMany (see Utils/Mongo/MongoService.cs) - that's
        // dead code repo-wide, not something to fix here since it'd affect every
        // other MongoService<T> subclass in production. This collection's unique
        // index on Id is created explicitly instead, since AC2's counter-scoping
        // guarantee genuinely depends on it (two racing upserts for the same new
        // key must not create duplicate documents).
        Collection.Indexes.CreateOne(
            new CreateIndexModel<RegulatoryNumberSequenceCounter>(
                Builders<RegulatoryNumberSequenceCounter>.IndexKeys.Ascending(c => c.Id),
                new CreateIndexOptions { Unique = true }
            )
        );
    }

    public async Task<int> IncrementAsync(string key, CancellationToken ct = default)
    {
        var filter = Builders<RegulatoryNumberSequenceCounter>.Filter.Eq(c => c.Id, key);
        var update = Builders<RegulatoryNumberSequenceCounter>
            .Update.Inc(c => c.CurrentMax, 1)
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        var result = await Collection.FindOneAndUpdateAsync(
            filter,
            update,
            new FindOneAndUpdateOptions<RegulatoryNumberSequenceCounter>
            {
                ReturnDocument = ReturnDocument.After,
                IsUpsert = true,
            },
            ct
        );

        return result.CurrentMax;
    }

    public async Task SeedIfHigherAsync(string key, int value, CancellationToken ct = default)
    {
        // $max is the atomic "raise the floor, never lower it" primitive - unlike a
        // filter-based Lt+upsert (which throws a duplicate-key error against the
        // unique index on Id when a document already exists but isn't below
        // `value`, since the filter as a whole then matches nothing), a plain
        // Eq(Id, key) filter always matches-or-upserts correctly, and $max itself
        // decides whether CurrentMax actually changes. Safe to re-run (AC4).
        var filter = Builders<RegulatoryNumberSequenceCounter>.Filter.Eq(c => c.Id, key);
        var update = Builders<RegulatoryNumberSequenceCounter>
            .Update.Max(c => c.CurrentMax, value)
            .Set(c => c.UpdatedAt, DateTime.UtcNow);

        await Collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public async Task<int?> GetCurrentMaxAsync(string key, CancellationToken ct = default)
    {
        var counter = await Collection.Find(c => c.Id == key).FirstOrDefaultAsync(ct);
        return counter?.CurrentMax;
    }

    protected override List<CreateIndexModel<RegulatoryNumberSequenceCounter>> DefineIndexes(
        IndexKeysDefinitionBuilder<RegulatoryNumberSequenceCounter> builder
    ) => [];
}
