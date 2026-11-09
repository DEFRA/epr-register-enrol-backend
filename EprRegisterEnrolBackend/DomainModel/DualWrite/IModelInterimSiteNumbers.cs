using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>
/// The interim site numbers the model already uses in a registration, current and previous, for the legacy
/// allocation to avoid (B8d). The migration can renumber a legacy interim site to a number legacy has never seen,
/// so with <c>DomainModel:DualWrite</c> on, a number that is free in legacy is not necessarily free.
/// </summary>
public interface IModelInterimSiteNumbers
{
    Task<IReadOnlyCollection<string>> GetAsync(
        ObjectId? registrationEntityId,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    );
}
