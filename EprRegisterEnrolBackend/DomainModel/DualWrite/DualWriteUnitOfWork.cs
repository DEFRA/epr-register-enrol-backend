using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// <c>DomainModel:DualWrite</c> on: the work runs in one transaction, and any failure rolls it all back, is
/// logged once and counted, and answers 500. See <see cref="IDualWriteUnitOfWork"/>. Before the work, the
/// application's registration group is migrated in the same transaction if it is not already (§6.2
/// ensure-migrated), so no write path has to remember to.
/// </summary>
public sealed class DualWriteUnitOfWork(
    IMongoDbClientFactory clientFactory,
    IApplicationGroupMigrator migrator,
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
            async (session, token) =>
            {
                // An id that is not an ObjectId matches no application; the work answers its own 404.
                if (ObjectId.TryParse(applicationId, out var id))
                {
                    await migrator.EnsureMigratedAsync(id, session, token);
                }

                return await work(session, token);
            },
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
