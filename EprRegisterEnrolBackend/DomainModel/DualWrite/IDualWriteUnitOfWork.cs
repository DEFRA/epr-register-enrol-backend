using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// Runs one write path's legacy write and its domain model writes together (design doc §6.2, phase 3).
/// </summary>
/// <remarks>
/// With <c>DomainModel:DualWrite</c> off, <paramref name="work"/> runs as it always has, with a null session: the
/// handler does its legacy write only. With it on, <paramref name="work"/> runs in one transaction and gets its
/// session, which it passes to every read and write, legacy and model alike. A failure then rolls back both and
/// answers 500 (D9): unlike the seed, a model-write failure is fatal to the request. The transaction can retry
/// <paramref name="work"/>, so it must read what it changes inside the callback and hold no state across calls.
/// </remarks>
public interface IDualWriteUnitOfWork
{
    /// <param name="operation">The write path, such as <c>AddOverseasSite</c>, for the log line and the metric.</param>
    /// <param name="applicationId">The application concerned, for the log line.</param>
    Task<IResult> RunAsync(
        string operation,
        string applicationId,
        Func<IClientSessionHandle?, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken = default
    );
}
