using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.DualWrite;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>
/// RA-705 (decided 2026-11-16): an application legacy holds but the model doesn't, such as a seed whose model write
/// failed, would otherwise answer 404 for good, because only a write migrates it and the operator can't reach a write
/// without a successful read. Serving it runs the dual-write unit of work for it first, which ensures it is migrated
/// and syncs it, then serves it from the model.
/// </summary>
public static class ApplicationReaderSelfHeal
{
    /// <summary>The operation named in the unit of work's failure log and metric.</summary>
    public const string Operation = "ServeOrMigrate";

    /// <summary>
    /// The source's version of <paramref name="legacy"/>, migrating it first when the source doesn't hold it. Null
    /// when it still can't be served (the migration failed, for example a group over the size limit), which the
    /// caller answers as 404. With the legacy reader the first serve always succeeds, so nothing is migrated.
    /// </summary>
    public static async Task<AccreditationApplicationModel?> ServeOrMigrateAsync(
        this IApplicationReader reader,
        AccreditationApplicationModel legacy,
        IDualWriteUnitOfWork unitOfWork,
        CancellationToken cancellationToken
    )
    {
        var served = await reader.ServeFromSourceAsync(legacy, cancellationToken);
        if (served is not null || legacy.Id is not { } id)
        {
            return served;
        }

        var migrated = await unitOfWork.RunAsync(
            Operation,
            id.ToString(),
            (_, _) => Task.FromResult(Results.Ok()),
            cancellationToken
        );
        return migrated is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 }
            ? await reader.ServeFromSourceAsync(legacy, cancellationToken)
            : null;
    }
}
