using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

public static class DomainModelCollections
{
    public const string Organisations = "organisations";
    public const string RegistrationVersions = "registrationVersions";
    public const string OverseasSiteVersions = "overseasSiteVersions";
    public const string ApplicationVersions = "applicationVersions";
    public const string AccreditationVersions = "accreditationVersions";
}

/// <summary>A domain model collection whose existence and indexes the schema health check can verify.</summary>
public interface IDomainModelCollection
{
    string CollectionName { get; }

    /// <summary>Names of the indexes this collection is meant to have (excluding the implicit <c>_id_</c>).</summary>
    IReadOnlyList<string> ExpectedIndexNames { get; }

    /// <summary>Names of the indexes the server has now. Empty when the collection does not exist yet.</summary>
    Task<IReadOnlyList<string>> GetIndexNamesAsync(CancellationToken cancellationToken);
}

public interface IVersionedPersistence<T> : IDomainModelCollection
    where T : class, IVersionedEntity
{
    VersionedMongoRepository<T> Versions { get; }
}

public interface IOrganisationPersistence : IDomainModelCollection
{
    IMongoCollection<OrganisationEntity> Documents { get; }
}

public interface IRegistrationPersistence : IVersionedPersistence<RegistrationEntity>;

public interface IOverseasSitePersistence : IVersionedPersistence<OverseasSiteEntity>;

public interface IApplicationVersionPersistence : IVersionedPersistence<ApplicationEntity>;

public interface IAccreditationPersistence : IVersionedPersistence<AccreditationEntity>;

/// <summary>
/// Creates a domain model collection's indexes when it is constructed (the base <see cref="MongoService{T}"/>
/// behaviour) and reports them for the health check. Registering the persistence classes is therefore what
/// "expands" the schema in each environment.
/// </summary>
public abstract class DomainModelPersistence<T>(
    IMongoDbClientFactory connectionFactory,
    string collectionName,
    ILoggerFactory loggerFactory
) : MongoService<T>(connectionFactory, collectionName, loggerFactory), IDomainModelCollection
{
    private const int NamespaceNotFound = 26;

    public string CollectionName => Collection.CollectionNamespace.CollectionName;

    public IReadOnlyList<string> ExpectedIndexNames =>
        DefineIndexes(Builders<T>.IndexKeys).Select(m => m.Options.Name).ToList();

    public async Task<IReadOnlyList<string>> GetIndexNamesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cursor = await Collection.Indexes.ListAsync(cancellationToken);
            var documents = await cursor.ToListAsync(cancellationToken);
            return documents.Select(d => d["name"].AsString).Where(n => n != "_id_").ToList();
        }
        catch (MongoCommandException ex) when (ex.Code == NamespaceNotFound)
        {
            return [];
        }
    }
}

public abstract class VersionedPersistence<T> : DomainModelPersistence<T>, IVersionedPersistence<T>
    where T : class, IVersionedEntity
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

public class OrganisationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
)
    : DomainModelPersistence<OrganisationEntity>(
        connectionFactory,
        DomainModelCollections.Organisations,
        loggerFactory
    ),
        IOrganisationPersistence
{
    public IMongoCollection<OrganisationEntity> Documents => Collection;

    protected override List<CreateIndexModel<OrganisationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<OrganisationEntity> builder
    ) =>
        [
            new(
                builder.Ascending("reExOrganisationId"),
                new CreateIndexOptions { Name = "reExOrganisationId_u", Unique = true }
            ),
            // Unique only where the number is known, so organisations without one never collide.
            new(
                builder.Ascending("organisationNumber"),
                new CreateIndexOptions<OrganisationEntity>
                {
                    Name = "organisationNumber_u",
                    Unique = true,
                    PartialFilterExpression = new BsonDocumentFilterDefinition<OrganisationEntity>(
                        new BsonDocument("organisationNumber", new BsonDocument("$type", "int"))
                    ),
                }
            ),
        ];
}

public class RegistrationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory,
    TimeProvider clock
)
    : VersionedPersistence<RegistrationEntity>(
        connectionFactory,
        DomainModelCollections.RegistrationVersions,
        loggerFactory,
        clock
    ),
        IRegistrationPersistence
{
    protected override List<CreateIndexModel<RegistrationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<RegistrationEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<RegistrationEntity>();
        indexes.Add(VersionedIndexes.ForeignKeyAsOf<RegistrationEntity>("organisationId"));
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "reExRegistrationId_current_u",
                builder.Ascending("reExRegistrationId"),
                VersionedIndexes.StringPresent("reExRegistrationId")
            )
        );
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "registrationNumber_current_u",
                builder.Ascending("registrationNumber"),
                VersionedIndexes.StringPresent("registrationNumber")
            )
        );
        return indexes;
    }
}

public class OverseasSitePersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory,
    TimeProvider clock
)
    : VersionedPersistence<OverseasSiteEntity>(
        connectionFactory,
        DomainModelCollections.OverseasSiteVersions,
        loggerFactory,
        clock
    ),
        IOverseasSitePersistence
{
    protected override List<CreateIndexModel<OverseasSiteEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<OverseasSiteEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<OverseasSiteEntity>();
        indexes.Add(VersionedIndexes.ForeignKeyAsOf<OverseasSiteEntity>("registrationId"));
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "registrationId_orsId_current_u",
                builder.Ascending("registrationId").Ascending("orsId"),
                VersionedIndexes.StringPresent("orsId")
            )
        );
        // Keeps siteNumber unique across all of a registration's current ORSs. A multikey unique index does not
        // stop the same value twice inside one document, so the write path also checks the ORS's own array.
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "registrationId_interimSiteNumber_current_u",
                builder.Ascending("registrationId").Ascending("interimSites.siteNumber"),
                VersionedIndexes.StringPresent("interimSites.siteNumber")
            )
        );
        return indexes;
    }
}

public class ApplicationVersionPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory,
    TimeProvider clock
)
    : VersionedPersistence<ApplicationEntity>(
        connectionFactory,
        DomainModelCollections.ApplicationVersions,
        loggerFactory,
        clock
    ),
        IApplicationVersionPersistence
{
    protected override List<CreateIndexModel<ApplicationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<ApplicationEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<ApplicationEntity>();
        indexes.Add(VersionedIndexes.ForeignKeyAsOf<ApplicationEntity>("registrationId"));
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "applicationReference_current_u",
                builder.Ascending("applicationReference"),
                VersionedIndexes.StringPresent("applicationReference")
            )
        );
        return indexes;
    }
}

public class AccreditationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory,
    TimeProvider clock
)
    : VersionedPersistence<AccreditationEntity>(
        connectionFactory,
        DomainModelCollections.AccreditationVersions,
        loggerFactory,
        clock
    ),
        IAccreditationPersistence
{
    protected override List<CreateIndexModel<AccreditationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<AccreditationEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<AccreditationEntity>();
        indexes.Add(VersionedIndexes.ForeignKeyAsOf<AccreditationEntity>("registrationId"));
        indexes.Add(
            new CreateIndexModel<AccreditationEntity>(
                builder.Ascending("year").Ascending("accreditationNumber"),
                new CreateIndexOptions { Name = "year_accreditationNumber" }
            )
        );
        // At most one accreditation per registration and year.
        indexes.Add(
            VersionedIndexes.CurrentUnique<AccreditationEntity>(
                "registrationId_year_current_u",
                builder.Ascending("registrationId").Ascending("year")
            )
        );
        // The approving application's accreditation, so a retried approval push is a no-op.
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "applicationId_current_u",
                builder.Ascending("applicationId"),
                new BsonDocument("applicationId", new BsonDocument("$type", "objectId"))
            )
        );
        // Globally unique across organisations.
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "accreditationNumber_current_u",
                builder.Ascending("accreditationNumber"),
                VersionedIndexes.StringPresent("accreditationNumber")
            )
        );
        return indexes;
    }
}
