using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Registrations;

/// <summary>
/// RA-697: the domain model's ORS versions (<c>overseasSiteVersions</c>), readable whether or not
/// <c>DomainModel:ExpandEnabled</c> is on, for the same reasons and in the same way as
/// <see cref="RegistrationVersionStore"/>.
/// </summary>
public sealed class OverseasSiteVersionStore(
    Func<IVersionedPersistence<OverseasSiteEntity>?> persistence,
    IMongoDbClientFactory connectionFactory,
    TimeProvider clock
)
{
    private readonly Lazy<VersionedMongoRepository<OverseasSiteEntity>> _versions = new(
        () =>
            persistence()?.Versions
            ?? new VersionedMongoRepository<OverseasSiteEntity>(
                connectionFactory.GetClient(),
                connectionFactory.GetCollection<OverseasSiteEntity>(
                    DomainModelCollections.OverseasSiteVersions
                ),
                clock
            ),
        LazyThreadSafetyMode.PublicationOnly
    );

    public VersionedMongoRepository<OverseasSiteEntity> Versions => _versions.Value;

    /// <summary>The registration's Active ORSs (current versions), by the registration's entity id.</summary>
    public Task<List<OverseasSiteEntity>> FindActiveByRegistrationAsync(
        ObjectId registrationEntityId,
        CancellationToken cancellationToken
    ) =>
        Versions.FindAllCurrentAsync(
            Builders<OverseasSiteEntity>.Filter.Eq(x => x.RegistrationId, registrationEntityId)
                & Builders<OverseasSiteEntity>.Filter.Eq(x => x.Status, OverseasSiteStatus.Active),
            cancellationToken: cancellationToken
        );
}

public static class OverseasSiteVersionStoreServiceCollectionExtensions
{
    public static IServiceCollection AddOverseasSiteVersionStore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new OverseasSiteVersionStore(
            // Resolved on first use, not here: see RegistrationVersionStore.
            () => sp.GetService<IVersionedPersistence<OverseasSiteEntity>>(),
            sp.GetRequiredService<IMongoDbClientFactory>(),
            sp.GetRequiredService<TimeProvider>()
        ));
        return services;
    }
}
