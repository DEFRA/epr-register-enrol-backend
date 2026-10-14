using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public sealed class ChangeRequestPersistence
{
    private readonly IMongoCollection<ChangeRequest> _requests;

    public ChangeRequestPersistence(IMongoDbClientFactory factory)
    {
        _requests = factory.GetCollection<ChangeRequest>("accreditationChangeRequests");
        // Fail closed if the active-slot constraint cannot be installed.
        _requests.Indexes.CreateOne(new CreateIndexModel<ChangeRequest>(
            Builders<ChangeRequest>.IndexKeys
                .Ascending(r => r.Target.OrganisationId).Ascending(r => r.Target.RegistrationId)
                .Ascending(r => r.Target.AccreditationId).Ascending(r => r.Target.MaterialType)
                .Ascending(r => r.Target.Year),
            new CreateIndexOptions<ChangeRequest>
            {
                Name = "one_active_request_per_target", Unique = true,
                PartialFilterExpression = Builders<ChangeRequest>.Filter.Eq(r => r.Active, true),
            }));
    }

    public Task<ChangeRequest?> ReadActive(AccreditationChangeTarget target, CancellationToken ct) =>
        _requests.Find(TargetFilter(target) & Builders<ChangeRequest>.Filter.Eq(r => r.Active, true))
            .FirstOrDefaultAsync(ct)!;

    public Task<List<ChangeRequest>> ReadHistory(AccreditationChangeTarget target, CancellationToken ct) =>
        _requests.Find(TargetFilter(target) & Builders<ChangeRequest>.Filter.Eq(r => r.Active, false))
            .SortByDescending(r => r.CreatedAt).ToListAsync(ct);

    public Task<ChangeRequest?> Read(string requestId, CancellationToken ct) =>
        _requests.Find(r => r.RequestId == requestId).FirstOrDefaultAsync(ct)!;

    public async Task<bool> Refuse(ChangeRequestOutcome outcome, CancellationToken ct)
    {
        var filter = Builders<ChangeRequest>.Filter.Eq(r => r.RequestId, outcome.RequestId)
            & TargetFilter(outcome.Target)
            & Builders<ChangeRequest>.Filter.Eq(r => r.CaseId, outcome.CaseId)
            & Builders<ChangeRequest>.Filter.Eq(r => r.ProposedSiteId, outcome.ProposedSiteId)
            & Builders<ChangeRequest>.Filter.Eq(r => r.Active, true)
            & Builders<ChangeRequest>.Filter.Eq(r => r.Status, "submitted");
        var result = await _requests.UpdateOneAsync(filter, Builders<ChangeRequest>.Update
            .Set(r => r.Status, "rejected").Set(r => r.Active, false).Set(r => r.ClosedAt, DateTime.UtcNow),
            cancellationToken: ct);
        if (result.MatchedCount == 1) return true;
        var existing = await Read(outcome.RequestId, ct);
        return existing is { Active: false, Status: "rejected" }
            && existing.Target == outcome.Target && existing.CaseId == outcome.CaseId
            && existing.ProposedSiteId == outcome.ProposedSiteId;
    }

    public async Task<ChangeRequest> StartOrResume(ChangeRequest request, CancellationToken ct)
    {
        var existing = await ReadActive(request.Target, ct);
        if (existing is not null) return existing;
        try
        {
            await _requests.InsertOneAsync(request, cancellationToken: ct);
            return request;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return await ReadActive(request.Target, ct) ?? throw new InvalidOperationException("Active request disappeared during submission.");
        }
    }

    public async Task<ChangeRequest> ConfirmReceipt(ChangeRequest request, string caseId, string reference, CancellationToken ct)
    {
        var filter = Builders<ChangeRequest>.Filter.Eq(r => r.RequestId, request.RequestId)
            & Builders<ChangeRequest>.Filter.Eq(r => r.Active, true)
            & (Builders<ChangeRequest>.Filter.Eq(r => r.CaseId, null) | Builders<ChangeRequest>.Filter.Eq(r => r.CaseId, caseId));
        var updated = await _requests.FindOneAndUpdateAsync(filter, Builders<ChangeRequest>.Update
                .Set(r => r.CaseId, caseId).Set(r => r.CaseReference, reference).Set(r => r.Status, "submitted"),
            new FindOneAndUpdateOptions<ChangeRequest> { ReturnDocument = ReturnDocument.After }, ct);
        return updated ?? throw new InvalidOperationException("Conflicting change-request receipt.");
    }

    private static FilterDefinition<ChangeRequest> TargetFilter(AccreditationChangeTarget target) =>
        Builders<ChangeRequest>.Filter.Eq(r => r.Target.OrganisationId, target.OrganisationId)
        & Builders<ChangeRequest>.Filter.Eq(r => r.Target.RegistrationId, target.RegistrationId)
        & Builders<ChangeRequest>.Filter.Eq(r => r.Target.AccreditationId, target.AccreditationId)
        & Builders<ChangeRequest>.Filter.Eq(r => r.Target.MaterialType, target.MaterialType)
        & Builders<ChangeRequest>.Filter.Eq(r => r.Target.Year, target.Year);
}
