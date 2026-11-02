using System.Runtime.CompilerServices;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// The one shape a write handler uses to run in the dual-write unit of work (RA-644 B8): its body is a separate
/// <c>…Async</c> method that takes the session, so every read and write it makes is in the transaction, and the
/// operation is named after the handler for the failure log and metric.
/// </summary>
public static class UnitOfWorkHandlerExtensions
{
    public static Task<IResult> InTransactionAsync(
        this IDualWriteUnitOfWork unitOfWork,
        string applicationId,
        Func<IClientSessionHandle?, Task<IResult>> body,
        CancellationToken cancellationToken,
        [CallerMemberName] string operation = ""
    ) =>
        unitOfWork.RunAsync(
            operation,
            applicationId,
            (session, _) => body(session),
            cancellationToken
        );
}
