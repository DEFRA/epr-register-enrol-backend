using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Diagnostics;

/// <summary>
/// The result of <see cref="TransactionSmokeTest"/>. <see cref="ErrorKind"/> names an exception type (and server
/// code) only, never a message, so it is safe to log.
/// </summary>
public record TransactionSmokeResult(bool CommitWorks, bool AbortWorks, string? ErrorKind)
{
    public bool Supported => CommitWorks && AbortWorks;
}

/// <summary>
/// B0 / A10: proves the Mongo deployment supports multi-document transactions, which the versioned domain model's
/// close-then-insert write depends on. Writes one throwaway document to a scratch collection: once inside a
/// transaction that is aborted (it must vanish), once inside one that commits (it must appear), then removes it.
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
            using var session = await database.Client.StartSessionAsync(
                cancellationToken: cancellationToken
            );

            var aborted = new BsonDocument("_id", ObjectId.GenerateNewId());
            session.StartTransaction();
            await scratch.InsertOneAsync(session, aborted, cancellationToken: cancellationToken);
            await session.AbortTransactionAsync(cancellationToken);
            abortWorks =
                await scratch.CountDocumentsAsync(
                    new BsonDocument("_id", aborted["_id"]),
                    cancellationToken: cancellationToken
                ) == 0;

            var committed = new BsonDocument("_id", ObjectId.GenerateNewId());
            await session.WithTransactionAsync(
                async (s, token) =>
                {
                    await scratch.InsertOneAsync(s, committed, cancellationToken: token);
                    return true;
                },
                cancellationToken: cancellationToken
            );
            commitWorks =
                await scratch.CountDocumentsAsync(
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
