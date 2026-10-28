using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// <c>DomainModel:DualWrite</c> on: the work runs in one transaction, and any failure rolls it all back, is
/// logged once and counted, and answers 500. See <see cref="IDualWriteUnitOfWork"/>.
/// </summary>
public sealed class DualWriteUnitOfWork(
    IMongoDbClientFactory clientFactory,
    DualWriteMetrics metrics,
    ILogger<DualWriteUnitOfWork> logger
) : IDualWriteUnitOfWork
{
    public async Task<IResult> RunAsync(
        string operation,
        string applicationId,
        Func<IClientSessionHandle?, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            return await RunWithRetryAsync(operation, applicationId, work, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordFailure(operation);
            // Ids and the error kind only; the exception is not logged (see MongoErrors.Kind).
            logger.LogError(
                "Dual-write {Operation} failed and was rolled back: application {ApplicationId}, error {ErrorKind}",
                operation,
                applicationId,
                MongoErrors.Kind(ex)
            );
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The change could not be saved."
            );
        }
    }

    private Task<IResult> RunWithRetryAsync(
        string operation,
        string applicationId,
        Func<IClientSessionHandle?, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken
    ) =>
        InsertRaceRetry.RunInTransactionAsync(
            clientFactory,
            (session, token) => work(session, token),
            attempt =>
                logger.LogWarning(
                    "Dual-write {Operation} lost an insert race and will retry: application {ApplicationId}, attempt {Attempt}",
                    operation,
                    applicationId,
                    attempt
                ),
            cancellationToken
        );
}
