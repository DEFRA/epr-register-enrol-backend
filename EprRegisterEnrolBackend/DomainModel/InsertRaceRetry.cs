using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel;

/// <summary>
/// Runs work in one transaction, retrying the whole transaction when it loses an insert race. A concurrent writer
/// can commit a first version between our read and our insert, so a partial unique index rejects ours; the retry
/// reads it. <c>WithTransactionAsync</c> already retries transient errors (write conflicts while the rival
/// transaction is still open); this covers the duplicate key once the rival has committed.
/// </summary>
internal static class InsertRaceRetry
{
    public const int MaxAttempts = 3;

    /// <param name="onRetry">Called with the failed attempt's number before each retry, to log it.</param>
    public static async Task<T> RunInTransactionAsync<T>(
        IMongoDbClientFactory clientFactory,
        Func<IClientSessionHandle, CancellationToken, Task<T>> work,
        Action<int> onRetry,
        CancellationToken cancellationToken
    )
    {
        using var session = await clientFactory
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await session.WithTransactionAsync(
                    work,
                    cancellationToken: cancellationToken
                );
            }
            catch (MongoException ex) when (attempt < MaxAttempts && MongoErrors.IsDuplicateKey(ex))
            {
                onRetry(attempt);
            }
        }
    }
}
