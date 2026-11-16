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
        // RA-706: index for current versions by case management work item ID.
        indexes.Add(
            VersionedIndexes.Current(
                "caseManagementWorkItemId_current",
                builder.Ascending(x => x.CaseManagementWorkItemId)
            )
        );
        // RA-706: index for current versions by re-ex organisation ID and type.
        indexes.Add(
            VersionedIndexes.Current(
                "reExOrganisationId_current",
                builder.Ascending(x => x.ReExOrganisationId).Ascending(x => x.ApplicationType)
            )
        );
        // RA-706: index for current versions by re-ex registration ID and type.
        indexes.Add(
            VersionedIndexes.Current(
                "reExRegistrationId_current",
                builder.Ascending(x => x.ReExRegistrationId).Ascending(x => x.ApplicationType)
            )
        );
        // RA-706: index for current versions by reserved accreditation number.
        indexes.Add(
            VersionedIndexes.Current(
                "reservedAccreditationNumber_current",
                Builders<ApplicationEntity>.IndexKeys.Ascending(ApplicationFields.ReservedAccreditationNumber)
            )
        );
        return indexes;
    }

    // A backstop against any writer saving an unknown type or another type's fields (RA-746).
    protected override BsonDocument? DefineValidator() => ApplicationVersionSchema.Validator();
}
