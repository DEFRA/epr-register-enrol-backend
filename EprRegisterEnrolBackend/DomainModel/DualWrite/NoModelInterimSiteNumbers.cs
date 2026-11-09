using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary><c>DomainModel:DualWrite</c> off: there is no model to avoid.</summary>
public sealed class NoModelInterimSiteNumbers : IModelInterimSiteNumbers
{
    public Task<IReadOnlyCollection<string>> GetAsync(
        ObjectId? registrationEntityId,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlyCollection<string>>([]);
}
