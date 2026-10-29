using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
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

/// <summary><c>DomainModel:DualWrite</c> off: there is no model to avoid.</summary>
public sealed class NoModelInterimSiteNumbers : IModelInterimSiteNumbers
{
    public Task<IReadOnlyCollection<string>> GetAsync(
        ObjectId? registrationEntityId,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<IReadOnlyCollection<string>>([]);
}

public sealed class ModelInterimSiteNumbers(ApplicationGroupStores stores)
    : IModelInterimSiteNumbers
{
    public async Task<IReadOnlyCollection<string>> GetAsync(
        ObjectId? registrationEntityId,
        IClientSessionHandle? session,
        CancellationToken cancellationToken = default
    )
    {
        if (registrationEntityId is not { } registrationId)
        {
            return [];
        }

        var sites = await stores.OverseasSites.Versions.FindAllCurrentAsync(
            Builders<OverseasSiteEntity>.Filter.Eq(s => s.RegistrationId, registrationId),
            session,
            cancellationToken
        );
        return sites
            .SelectMany(s => s.InterimSites)
            .SelectMany(i => i.PreviousSiteNumbers.Prepend(i.SiteNumber))
            .ToHashSet();
    }
}
