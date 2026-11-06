using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationChanges;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// In-memory IChangeRequestPersistence with the same contract as the Mongo one: one InProgress
// request per accreditation and optimistic concurrency on Version. Stores deep copies so a test
// can't pass by mutating the object an endpoint handed back.
public class FakeChangeRequestPersistence : IChangeRequestPersistence
{
    private readonly Lock _lock = new();
    private readonly List<AccreditationChangeRequest> _requests = [];

    public IReadOnlyList<AccreditationChangeRequest> All
    {
        get
        {
            lock (_lock)
                return _requests.Select(Copy).ToList();
        }
    }

    public void Seed(AccreditationChangeRequest request)
    {
        lock (_lock)
            _requests.Add(Copy(request));
    }

    public Task<AccreditationChangeRequest?> GetDraftAsync(
        string organisationId,
        string applicationId,
        CancellationToken ct = default
    )
    {
        lock (_lock)
        {
            var draft = _requests.Find(r =>
                r.OrganisationId == organisationId
                && r.ApplicationId == applicationId
                && r.Status == ChangeRequestStatus.InProgress
            );
            return Task.FromResult(draft is null ? null : Copy(draft));
        }
    }

    public Task<List<AccreditationChangeRequest>> ListAsync(
        string organisationId,
        string applicationId,
        CancellationToken ct = default
    )
    {
        lock (_lock)
            return Task.FromResult(
                _requests
                    .Where(r => r.OrganisationId == organisationId && r.ApplicationId == applicationId)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(Copy)
                    .ToList()
            );
    }

    public Task<bool> InsertAsync(AccreditationChangeRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (
                _requests.Exists(r =>
                    r.OrganisationId == request.OrganisationId
                    && r.ApplicationId == request.ApplicationId
                    && r.Status == ChangeRequestStatus.InProgress
                )
            )
                return Task.FromResult(false);
            _requests.Add(Copy(request));
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Makes an update fail as a lost race (another writer moved the version) when it returns true
    /// for the request being saved.
    /// </summary>
    public Func<AccreditationChangeRequest, bool>? LoseUpdateWhen { get; set; }

    public int UpdateCount { get; private set; }

    public Task<bool> UpdateAsync(AccreditationChangeRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            UpdateCount++;
            if (LoseUpdateWhen?.Invoke(request) == true)
                return Task.FromResult(false);
            var index = _requests.FindIndex(r =>
                r.RequestId == request.RequestId && r.Version == request.Version
            );
            if (index < 0)
                return Task.FromResult(false);
            if (
                request.Status == ChangeRequestStatus.InProgress
                && _requests.Exists(r =>
                    r.RequestId != request.RequestId
                    && r.OrganisationId == request.OrganisationId
                    && r.ApplicationId == request.ApplicationId
                    && r.Status == ChangeRequestStatus.InProgress
                )
            )
                return Task.FromResult(false);
            request.Version++;
            request.UpdatedAt = DateTime.UtcNow;
            _requests[index] = Copy(request);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteDraftAsync(
        AccreditationChangeRequest request,
        CancellationToken ct = default
    )
    {
        lock (_lock)
            return Task.FromResult(
                _requests.RemoveAll(r =>
                    r.RequestId == request.RequestId
                    && r.Version == request.Version
                    && r.Status == ChangeRequestStatus.InProgress
                ) == 1
            );
    }

    private static AccreditationChangeRequest Copy(AccreditationChangeRequest request) =>
        JsonSerializer.Deserialize<AccreditationChangeRequest>(JsonSerializer.Serialize(request))!;
}
