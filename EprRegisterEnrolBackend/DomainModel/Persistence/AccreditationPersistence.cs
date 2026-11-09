using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

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
    )
{
    protected override List<CreateIndexModel<AccreditationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<AccreditationEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<AccreditationEntity>();
        indexes.Add(
            VersionedIndexes.ForeignKeyAsOf(
                "registrationId_asof",
                builder.Ascending(x => x.RegistrationId)
            )
        );
        indexes.Add(
            new CreateIndexModel<AccreditationEntity>(
                builder.Ascending(x => x.Year).Ascending(x => x.AccreditationNumber),
                new CreateIndexOptions { Name = "year_accreditationNumber" }
            )
        );
        // At most one accreditation per registration and year.
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "registrationId_year_current_u",
                builder.Ascending(x => x.RegistrationId).Ascending(x => x.Year)
            )
        );
        // The approving application's accreditation, so a retried approval push is a no-op.
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "applicationId_current_u",
                builder.Ascending(x => x.ApplicationId),
                Builders<AccreditationEntity>.Filter.Type(x => x.ApplicationId, BsonType.ObjectId)
            )
        );
        // Globally unique across organisations.
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "accreditationNumber_current_u",
                builder.Ascending(x => x.AccreditationNumber),
                VersionedIndexes.StringPresent<AccreditationEntity>(x => x.AccreditationNumber)
            )
        );
        return indexes;
    }
}
