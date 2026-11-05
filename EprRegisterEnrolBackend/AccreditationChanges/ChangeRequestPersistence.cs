using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public interface IChangeRequestPersistence
{
    /// <summary>The single InProgress request for the accreditation, if any.</summary>
    Task<AccreditationChangeRequest?> GetDraftAsync(
        string organisationId,
        string applicationId,
        CancellationToken ct = default
    );

    /// <summary>Every request for the accreditation, newest first.</summary>
    Task<List<AccreditationChangeRequest>> ListAsync(
        string organisationId,
        string applicationId,
        CancellationToken ct = default
    );

    /// <summary>False when another InProgress request already exists for the accreditation.</summary>
    Task<bool> InsertAsync(AccreditationChangeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Replaces the request if nobody else changed it since it was read (optimistic
    /// concurrency on <see cref="AccreditationChangeRequest.Version"/>); false otherwise.
    /// </summary>
    Task<bool> UpdateAsync(AccreditationChangeRequest request, CancellationToken ct = default);

    /// <summary>Deletes an InProgress request at the version read; false otherwise.</summary>
    Task<bool> DeleteDraftAsync(AccreditationChangeRequest request, CancellationToken ct = default);
}

public class ChangeRequestPersistence
    : MongoService<AccreditationChangeRequest>,
        IChangeRequestPersistence
{
    internal const string CollectionName = "accreditationChangeRequests";
    internal const string OneDraftIndexName = "one_in_progress_request_per_accreditation";

    public ChangeRequestPersistence(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, CollectionName, loggerFactory) { }

    private static FilterDefinitionBuilder<AccreditationChangeRequest> Filter =>
        Builders<AccreditationChangeRequest>.Filter;

    private static FilterDefinition<AccreditationChangeRequest> ForAccreditation(
        string organisationId,
        string applicationId
    ) =>
        Filter.Eq(r => r.OrganisationId, organisationId)
        & Filter.Eq(r => r.ApplicationId, applicationId);

    public async Task<AccreditationChangeRequest?> GetDraftAsync(
        string organisationId,
        string applicationId,
        CancellationToken ct = default
    ) =>
        await Collection
            .Find(
                ForAccreditation(organisationId, applicationId)
                    & Filter.Eq(r => r.Status, ChangeRequestStatus.InProgress)
            )
            .FirstOrDefaultAsync(ct);

    public Task<List<AccreditationChangeRequest>> ListAsync(
        string organisationId,
        string applicationId,
        CancellationToken ct = default
    ) =>
        Collection
            .Find(ForAccreditation(organisationId, applicationId))
            .SortByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

    public async Task<bool> InsertAsync(
        AccreditationChangeRequest request,
        CancellationToken ct = default
    )
    {
        try
        {
            await Collection.InsertOneAsync(request, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex)
            when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public async Task<bool> UpdateAsync(
        AccreditationChangeRequest request,
        CancellationToken ct = default
    )
    {
        var readVersion = request.Version;
        request.Version = readVersion + 1;
        request.UpdatedAt = DateTime.UtcNow;
        try
        {
            var result = await Collection.ReplaceOneAsync(
                Filter.Eq(r => r.RequestId, request.RequestId)
                    & Filter.Eq(r => r.Version, readVersion),
                request,
                cancellationToken: ct
            );
            if (result.MatchedCount == 1)
                return true;
        }
        // Reopening a request as InProgress when another draft exists breaks the one-draft rule.
        catch (MongoWriteException ex)
            when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey) { }
        request.Version = readVersion;
        return false;
    }

    public async Task<bool> DeleteDraftAsync(
        AccreditationChangeRequest request,
        CancellationToken ct = default
    )
    {
        var result = await Collection.DeleteOneAsync(
            Filter.Eq(r => r.RequestId, request.RequestId)
                & Filter.Eq(r => r.Version, request.Version)
                & Filter.Eq(r => r.Status, ChangeRequestStatus.InProgress),
            ct
        );
        return result.DeletedCount == 1;
    }

    protected override List<CreateIndexModel<AccreditationChangeRequest>> DefineIndexes(
        IndexKeysDefinitionBuilder<AccreditationChangeRequest> builder
    ) =>
        [
            // At most one draft per accreditation: "Add another request" (AC10) adds a site to
            // the same draft rather than opening a second one, so one declaration and one charge
            // cover every site the operator is adding.
            new(
                builder.Ascending(r => r.OrganisationId).Ascending(r => r.ApplicationId),
                new CreateIndexOptions<AccreditationChangeRequest>
                {
                    Name = OneDraftIndexName,
                    Unique = true,
                    PartialFilterExpression = Filter.Eq(
                        r => r.Status,
                        ChangeRequestStatus.InProgress
                    ),
                }
            ),
            new(
                builder
                    .Ascending(r => r.OrganisationId)
                    .Ascending(r => r.ApplicationId)
                    .Descending(r => r.CreatedAt),
                new CreateIndexOptions { Name = "by_accreditation_newest_first" }
            ),
        ];
}
