using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>A versioned domain model collection: its indexes, plus the repository that reads and writes its versions.</summary>
public abstract class VersionedPersistence<T> : DomainModelPersistence<T>, IVersionedPersistence<T>
    where T : VersionedEntityBase
{
    protected VersionedPersistence(
        IMongoDbClientFactory connectionFactory,
        string collectionName,
        ILoggerFactory loggerFactory,
        TimeProvider clock
    )
        : base(connectionFactory, collectionName, loggerFactory)
    {
        Versions = new VersionedMongoRepository<T>(Client, Collection, clock);
    }

    public VersionedMongoRepository<T> Versions { get; }
}
