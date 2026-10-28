using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

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
    )
{
    protected override List<CreateIndexModel<RegistrationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<RegistrationEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<RegistrationEntity>();
        indexes.Add(
            VersionedIndexes.ForeignKeyAsOf(
                "organisationId_asof",
                builder.Ascending(x => x.OrganisationId)
            )
        );
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "reExRegistrationId_current_u",
                builder.Ascending(x => x.ReExRegistrationId),
                VersionedIndexes.StringPresent<RegistrationEntity>(x => x.ReExRegistrationId)
            )
        );
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "registrationNumber_current_u",
                builder.Ascending(x => x.RegistrationNumber),
                VersionedIndexes.StringPresent<RegistrationEntity>(x => x.RegistrationNumber)
            )
        );
        return indexes;
    }
}
