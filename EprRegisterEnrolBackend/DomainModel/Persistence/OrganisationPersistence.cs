using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// The <c>organisations</c> collection and its indexes. Organisations are not versioned (D7). Nothing reads or
/// writes them yet; the sync that will (Q15) adds its operations here.
/// </summary>
public class OrganisationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
)
    : DomainModelPersistence<OrganisationEntity>(
        connectionFactory,
        DomainModelCollections.Organisations,
        loggerFactory
    )
{
    protected override List<CreateIndexModel<OrganisationEntity>> DefineIndexes(
        IndexKeysDefinitionBuilder<OrganisationEntity> builder
    ) =>
        [
            new(
                builder.Ascending(x => x.ReExOrganisationId),
                new CreateIndexOptions { Name = "reExOrganisationId_u", Unique = true }
            ),
            // Unique only where the number is known, so organisations without one never collide.
            new(
                builder.Ascending(x => x.OrganisationNumber),
                new CreateIndexOptions<OrganisationEntity>
                {
                    Name = "organisationNumber_u",
                    Unique = true,
                    PartialFilterExpression = Builders<OrganisationEntity>.Filter.Type(
                        x => x.OrganisationNumber,
                        BsonType.Int32
                    ),
                }
            ),
        ];
}
