using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// <c>DomainModel:DualWrite</c> on: the work runs in one transaction, and any failure rolls it all back, is
/// logged once and counted, and answers 500. See <see cref="IDualWriteUnitOfWork"/>. Before the work, the
/// application's registration group is migrated in the same transaction if it is not already (§6.2
/// ensure-migrated), and after it the model is brought in line with what the work wrote (B8c), so no write path has to
/// remember either.
/// </summary>
public sealed class DualWriteUnitOfWork(
    IMongoDbClientFactory clientFactory,
    IApplicationGroupMigrator migrator,
    IApplicationModelSync sync,
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
        catch (DomainRuleViolationException ex)
        {
            // A rule, not a fault: every retry fails the same way, so the caller is told which rule, not asked to
            // try again. The rule is a fixed code; the message, which can name ids, is not logged.
            metrics.RecordFailure(operation);
            logger.LogWarning(
                "Dual-write {Operation} broke the rule {Rule} and was rolled back: application {ApplicationId}",
                operation,
                ex.Rule,
                applicationId
            );
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The change breaks a rule of the domain model.",
                extensions: new Dictionary<string, object?> { ["rule"] = ex.Rule.ToString() }
            );
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
                if (!ObjectId.TryParse(applicationId, out var id))
                {
                    return await work(session, token);
                }

                await migrator.EnsureMigratedAsync(id, session, token);
                var result = await work(session, token);
                await sync.SyncAsync(id, session, token);
                return result;
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
