using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
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
        // Every type shares the collection, so a registration's applications are listed by type (RA-745).
        indexes.Add(
            VersionedIndexes.Current(
                "registrationId_applicationType_current",
                builder.Ascending(x => x.RegistrationId).Ascending(x => x.ApplicationType)
            )
        );
        // The backstop for rules chosen by type, such as one open change per ORS: only documents carrying a key take
        // part, so a missing key never collides (RA-747).
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "exclusivityKey_current_u",
                builder.Ascending(x => x.ExclusivityKey),
                VersionedIndexes.StringPresent<ApplicationEntity>(x => x.ExclusivityKey)
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

    // A backstop against any writer saving an unknown type or another type's fields (RA-746).
    protected override BsonDocument? DefineValidator() => ApplicationVersionSchema.Validator();
}
