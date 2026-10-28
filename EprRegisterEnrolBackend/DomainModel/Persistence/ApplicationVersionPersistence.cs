using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

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
    )
{
    protected override List<CreateIndexModel<ApplicationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<ApplicationEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<ApplicationEntity>();
        indexes.Add(
            VersionedIndexes.ForeignKeyAsOf(
                "registrationId_asof",
                builder.Ascending(x => x.RegistrationId)
            )
        );
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "applicationReference_current_u",
                builder.Ascending(x => x.ApplicationReference),
                VersionedIndexes.StringPresent<ApplicationEntity>(x => x.ApplicationReference)
            )
        );
        return indexes;
    }
}
