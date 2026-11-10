using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Diagnostics;

/// <summary>
/// B0 / A10: proves the Mongo deployment supports multi-document transactions, which the versioned domain model's
/// close-then-insert write depends on. Writes one throwaway document to a scratch collection: once inside a
/// transaction that is aborted (it must vanish), once inside one that commits and reads it back (it must appear),
/// then removes it. Both start with <see cref="DomainModelTransactions.Options"/>, as the domain model's own do.
/// </summary>
public static class TransactionSmokeTest
{
    public const string ScratchCollection = "domainModelDiagnostics";

    public static async Task<TransactionSmokeResult> RunAsync(
        IMongoDatabase database,
        CancellationToken cancellationToken
    )
    {
        var abortWorks = false;
        var commitWorks = false;
        try
        {
            // Creating a collection inside a transaction is not supported everywhere, so do it first.
            if (!await CollectionExists(database, cancellationToken))
            {
                await database.CreateCollectionAsync(
                    ScratchCollection,
                    cancellationToken: cancellationToken
                );
            }

            var scratch = database.GetCollection<BsonDocument>(ScratchCollection);
            // The checks after each transaction read from the primary, so a lagging secondary can't fail them.
            var primary = scratch.WithReadPreference(ReadPreference.Primary);
            using var session = await database.Client.StartSessionAsync(
                cancellationToken: cancellationToken
            );

            var aborted = new BsonDocument("_id", ObjectId.GenerateNewId());
            session.StartTransaction(DomainModelTransactions.Options);
            await scratch.InsertOneAsync(session, aborted, cancellationToken: cancellationToken);
            await session.AbortTransactionAsync(cancellationToken);
            abortWorks =
                await primary.CountDocumentsAsync(
                    new BsonDocument("_id", aborted["_id"]),
                    cancellationToken: cancellationToken
                ) == 0;

            var committed = new BsonDocument("_id", ObjectId.GenerateNewId());
            // A read inside the transaction, as every dual-write makes. The driver refuses one unless the
            // transaction reads from the primary, so this is what shows the options work with this connection.
            var seesItsOwnWrite = await session.WithTransactionAsync(
                async (s, token) =>
                {
                    await scratch.InsertOneAsync(s, committed, cancellationToken: token);
                    return await scratch.CountDocumentsAsync(
                            s,
                            new BsonDocument("_id", committed["_id"]),
                            cancellationToken: token
                        ) == 1;
                },
                DomainModelTransactions.Options,
                cancellationToken
            );
            commitWorks =
                seesItsOwnWrite
                && await primary.CountDocumentsAsync(
                    new BsonDocument("_id", committed["_id"]),
                    cancellationToken: cancellationToken
                ) == 1;
            await scratch.DeleteOneAsync(
                new BsonDocument("_id", committed["_id"]),
                cancellationToken
            );

            return new TransactionSmokeResult(commitWorks, abortWorks, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var code = ex is MongoCommandException command ? $":{command.Code}" : string.Empty;
            return new TransactionSmokeResult(commitWorks, abortWorks, ex.GetType().Name + code);
        }
    }

    private static async Task<bool> CollectionExists(
        IMongoDatabase database,
        CancellationToken cancellationToken
    )
    {
        using var cursor = await database.ListCollectionNamesAsync(
            new ListCollectionNamesOptions { Filter = new BsonDocument("name", ScratchCollection) },
            cancellationToken
        );
        return await cursor.AnyAsync(cancellationToken);
    }
}
