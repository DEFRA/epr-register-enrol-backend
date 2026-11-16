using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Registrations;

/// <summary>
/// The domain model's registration versions (<c>registrationVersions</c>), readable whether or not
/// <c>DomainModel:ExpandEnabled</c> is on. With it on, this is the registered
/// <see cref="RegistrationPersistence"/>'s repository. With it off, <see cref="RegistrationPersistence"/> is not
/// registered, and constructing one would build the collection's indexes, which is what the flag withholds; so
/// this reads the same collection through a plain repository instead. A collection that doesn't exist yet reads
/// as empty.
/// </summary>
/// <remarks>
/// The persistence is taken as a factory and resolved on first use, not injected: constructing it builds its
/// indexes synchronously against Mongo, and this store is a dependency of the dev seed hosted services, which
/// the host constructs on its startup thread. That build belongs to MongoIndexInitializerService, in the
/// background. A failed first use is retried on the next, rather than cached.
/// </remarks>
public sealed class RegistrationVersionStore(
    Func<IVersionedPersistence<RegistrationEntity>?> persistence,
    IMongoDbClientFactory connectionFactory,
    TimeProvider clock
)
{
    private readonly Lazy<VersionedMongoRepository<RegistrationEntity>> _versions = new(
        () =>
            persistence()?.Versions
            ?? new VersionedMongoRepository<RegistrationEntity>(
                connectionFactory.GetClient(),
                connectionFactory.GetCollection<RegistrationEntity>(
                    DomainModelCollections.RegistrationVersions
                ),
                clock
            ),
        LazyThreadSafetyMode.PublicationOnly
    );

    public VersionedMongoRepository<RegistrationEntity> Versions => _versions.Value;

    public Task<RegistrationEntity?> FindCurrentByReExRegistrationIdAsync(
        string reExRegistrationId,
        CancellationToken cancellationToken
    ) =>
        Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(x => x.ReExRegistrationId, reExRegistrationId),
            cancellationToken: cancellationToken
        );

    // RA-739: backed by RegistrationPersistence's registrationNumber_current_u index.
    public Task<RegistrationEntity?> FindCurrentByRegistrationNumberAsync(
        string registrationNumber,
        CancellationToken cancellationToken
    ) =>
        Versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(x => x.RegistrationNumber, registrationNumber),
            cancellationToken: cancellationToken
        );
}

public static class RegistrationVersionStoreServiceCollectionExtensions
{
    public static IServiceCollection AddRegistrationVersionStore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new RegistrationVersionStore(
            // Resolved on first use, not here: see RegistrationVersionStore.
            () => sp.GetService<IVersionedPersistence<RegistrationEntity>>(),
            sp.GetRequiredService<IMongoDbClientFactory>(),
            sp.GetRequiredService<TimeProvider>()
        ));
        return services;
    }
}
