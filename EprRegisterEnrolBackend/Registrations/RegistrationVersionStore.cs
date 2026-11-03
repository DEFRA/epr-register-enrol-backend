using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
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
public sealed class RegistrationVersionStore(
    IServiceProvider services,
    IMongoDbClientFactory connectionFactory,
    TimeProvider clock
)
{
    private readonly Lazy<VersionedMongoRepository<RegistrationEntity>> _versions = new(() =>
        services.GetService<IVersionedPersistence<RegistrationEntity>>()?.Versions
        ?? new VersionedMongoRepository<RegistrationEntity>(
            connectionFactory.GetClient(),
            connectionFactory.GetCollection<RegistrationEntity>(
                DomainModelCollections.RegistrationVersions
            ),
            clock
        )
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
}
