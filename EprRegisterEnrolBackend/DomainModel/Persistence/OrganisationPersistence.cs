using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// The <c>organisations</c> collection and its indexes. Organisations are not versioned (D7). Seed dual-write
/// (phase 2) upserts them; the sync (Q15) will add its operations here.
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
    /// <summary>
    /// Creates the organisation for <paramref name="reExOrganisationId"/>, or overwrites it in place (D7), with
    /// <paramref name="apply"/> run on the stored copy so fields it does not touch are kept.
    /// </summary>
    /// <returns>The organisation's <c>_id</c>.</returns>
    public async Task<ObjectId> UpsertAsync(
        string reExOrganisationId,
        Action<OrganisationEntity> apply,
        DateTime now,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var filter = Builders<OrganisationEntity>.Filter.Eq(
            x => x.ReExOrganisationId,
            reExOrganisationId
        );
        var organisation =
            await Collection.Find(session, filter).FirstOrDefaultAsync(cancellationToken)
            ?? new OrganisationEntity
            {
                Id = ObjectId.GenerateNewId(),
                ReExOrganisationId = reExOrganisationId,
            };
        apply(organisation);
        organisation.Source.ImportedAt = now;
        organisation.UpdatedAt = now;

        await Collection.ReplaceOneAsync(
            session,
            filter,
            organisation,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken
        );
        return organisation.Id;
    }

    /// <summary>
    /// The organisation for <paramref name="reExOrganisationId"/>, created from <paramref name="create"/> only when
    /// there is none, so an organisation the seed or a sync already holds is left exactly as it is.
    /// </summary>
    /// <returns>The organisation's <c>_id</c>.</returns>
    public async Task<ObjectId> GetOrCreateAsync(
        string reExOrganisationId,
        Func<OrganisationEntity> create,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var existing = await Collection
            .Find(
                session,
                Builders<OrganisationEntity>.Filter.Eq(
                    x => x.ReExOrganisationId,
                    reExOrganisationId
                )
            )
            .Project(x => x.Id)
            .ToListAsync(cancellationToken);
        if (existing.Count > 0)
        {
            return existing[0];
        }

        var organisation = create();
        organisation.Id = ObjectId.GenerateNewId();
        organisation.ReExOrganisationId = reExOrganisationId;
        // A concurrent first write that got here first fails this on reExOrganisationId_u; the caller retries.
        await Collection.InsertOneAsync(
            session,
            organisation,
            cancellationToken: cancellationToken
        );
        return organisation.Id;
    }

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
