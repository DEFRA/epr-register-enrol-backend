using EprRegisterEnrolBackend.DomainModel.Versioning;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>A versioned domain model collection, read and written through <see cref="Versions"/>.</summary>
public interface IVersionedPersistence<T> : IDomainModelCollection
    where T : VersionedEntityBase
{
    VersionedMongoRepository<T> Versions { get; }
}
