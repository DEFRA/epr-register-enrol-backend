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
    /// Fills in the organisation number, company address and submitter from <paramref name="values"/> where the
    /// organisation has none, such as one a seed wrote before it had them. What it already holds is left alone.
    /// Returns the organisation as stored.
    /// </summary>
    public async Task<OrganisationEntity> FillMissingDetailsAsync(
        ObjectId organisationId,
        OrganisationEntity values,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var filter = Builders<OrganisationEntity>.Filter.Eq(x => x.Id, organisationId);
        var stored = await Collection.Find(session, filter).SingleAsync(cancellationToken);
        var fillAddress =
            stored.FormattedRegisteredAddress is null
            && values.FormattedRegisteredAddress is not null;
        var fillSubmitter =
            stored.SubmitterContactDetails is null && values.SubmitterContactDetails is not null;
        if (fillAddress)
        {
            stored.FormattedRegisteredAddress = values.FormattedRegisteredAddress;
            stored.RegisteredAddress ??= values.RegisteredAddress;
            stored.IsUkRegisteredAddress = values.IsUkRegisteredAddress;
        }

        var fillPostcode =
            stored.RegisteredAddress?.Postcode is null
            && values.RegisteredAddress?.Postcode is not null;
        if (fillPostcode)
        {
            stored.RegisteredAddress ??= new PostalAddress();
            stored.RegisteredAddress.Postcode = values.RegisteredAddress!.Postcode;
        }

        if (fillSubmitter)
        {
            stored.SubmitterContactDetails = values.SubmitterContactDetails;
        }

        var fillNumber = stored.FillOrganisationNumber(values.OrganisationNumber);

        if (fillAddress || fillPostcode || fillSubmitter || fillNumber)
        {
            await Collection.ReplaceOneAsync(
                session,
                filter,
                stored,
                cancellationToken: cancellationToken
            );
        }

        return stored;
    }

    /// <summary>
    /// Gives the organisation with <paramref name="reExOrganisationId"/> its organisation number by
    /// <see cref="OrganisationEntity.FillOrganisationNumber"/>, so only while it has none. A number another
    /// organisation already holds fails on the unique index: two organisations sharing one is a data error to surface,
    /// never to work around.
    /// </summary>
    public async Task FillMissingOrganisationNumberAsync(
        string reExOrganisationId,
        int organisationNumber,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var organisation = await FindByReExOrganisationIdAsync(
            reExOrganisationId,
            session,
            cancellationToken
        );
        if (organisation?.FillOrganisationNumber(organisationNumber) != true)
        {
            return;
        }

        await Collection.UpdateOneAsync(
            session,
            Builders<OrganisationEntity>.Filter.Eq(x => x.Id, organisation.Id)
                & Builders<OrganisationEntity>.Filter.Eq(x => x.OrganisationNumber, null),
            Builders<OrganisationEntity>.Update.Set(x => x.OrganisationNumber, organisationNumber),
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// RA-764: every organisation held under <paramref name="reExOrganisationId"/> or numbered
    /// <paramref name="organisationNumber"/>. A list, so a caller correcting one organisation can see (and refuse to
    /// act on) the two keys pointing at different documents.
    /// </summary>
    public async Task<IReadOnlyList<OrganisationEntity>> FindByReExOrganisationIdOrNumberAsync(
        string reExOrganisationId,
        int organisationNumber,
        CancellationToken cancellationToken = default
    ) =>
        await Collection
            .Find(
                Builders<OrganisationEntity>.Filter.Eq(x => x.ReExOrganisationId, reExOrganisationId)
                    | Builders<OrganisationEntity>.Filter.Eq(
                        x => x.OrganisationNumber,
                        organisationNumber
                    )
            )
            .ToListAsync(cancellationToken);

    /// <summary>
    /// RA-764: sets the organisation's name to <paramref name="newName"/> only while it still holds
    /// <paramref name="expectedName"/>, touching no other field. Returns whether it was changed.
    /// </summary>
    public async Task<bool> RenameAsync(
        ObjectId organisationId,
        string expectedName,
        string newName,
        CancellationToken cancellationToken = default
    )
    {
        var result = await Collection.UpdateOneAsync(
            Builders<OrganisationEntity>.Filter.Eq(x => x.Id, organisationId)
                & Builders<OrganisationEntity>.Filter.Eq(x => x.Name, expectedName),
            Builders<OrganisationEntity>.Update.Set(x => x.Name, newName),
            cancellationToken: cancellationToken
        );
        return result.ModifiedCount == 1;
    }

    /// <summary>The organisation with <paramref name="reExOrganisationId"/>, or null.</summary>
    public async Task<OrganisationEntity?> FindByReExOrganisationIdAsync(
        string reExOrganisationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) =>
        await Collection
            .Find(
                session,
                Builders<OrganisationEntity>.Filter.Eq(
                    x => x.ReExOrganisationId,
                    reExOrganisationId
                )
            )
            .FirstOrDefaultAsync(cancellationToken);

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
