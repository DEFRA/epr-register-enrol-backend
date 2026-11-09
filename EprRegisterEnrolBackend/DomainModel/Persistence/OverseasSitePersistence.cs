using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

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
    )
{
    // A path into the embedded interim sites array, which has no typed form for an index key.
    private const string InterimSiteNumber = "interimSites.siteNumber";

    protected override List<CreateIndexModel<OverseasSiteEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<OverseasSiteEntity> builder
    )
    {
        var indexes = VersionedIndexes.Identity<OverseasSiteEntity>();
        indexes.Add(
            VersionedIndexes.ForeignKeyAsOf(
                "registrationId_asof",
                builder.Ascending(x => x.RegistrationId)
            )
        );
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "registrationId_orsId_current_u",
                builder.Ascending(x => x.RegistrationId).Ascending(x => x.OrsId),
                VersionedIndexes.StringPresent<OverseasSiteEntity>(x => x.OrsId)
            )
        );
        // Keeps siteNumber unique across all of a registration's current ORSs. A multikey unique index does not
        // stop the same value twice inside one document, so the write path also checks the ORS's own array.
        indexes.Add(
            VersionedIndexes.CurrentUnique(
                "registrationId_interimSiteNumber_current_u",
                builder.Ascending(x => x.RegistrationId).Ascending(InterimSiteNumber),
                VersionedIndexes.StringPresent<OverseasSiteEntity>(InterimSiteNumber)
            )
        );
        return indexes;
    }
}
