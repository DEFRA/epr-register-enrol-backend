using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary><c>DomainModel:DualWrite</c> on: the numbers the registration's ORSs hold, current and previous.</summary>
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
