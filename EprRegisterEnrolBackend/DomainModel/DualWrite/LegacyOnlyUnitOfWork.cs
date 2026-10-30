using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary><c>DomainModel:DualWrite</c> off: the work runs as before, with no session and no transaction.</summary>
public sealed class LegacyOnlyUnitOfWork : IDualWriteUnitOfWork
{
    public Task<IResult> RunAsync(
        string operation,
        string applicationId,
        Func<IClientSessionHandle?, CancellationToken, Task<IResult>> work,
        CancellationToken cancellationToken = default
    ) => work(null, cancellationToken);
}
