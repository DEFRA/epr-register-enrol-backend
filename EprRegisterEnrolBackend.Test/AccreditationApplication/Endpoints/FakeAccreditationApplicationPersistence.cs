using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class FakeAccreditationApplicationPersistence : IAccreditationApplicationPersistence
{
    private readonly List<AccreditationApplicationModel> _store = [];

    public void Seed(AccreditationApplicationModel application) => _store.Add(application);

    public void Clear()
    {
        _store.Clear();
        FailNextUpdate = false;
        FailNextOrsIdWrites = 0;
    }

    /// <summary>
    /// When set, the next call to <see cref="UpdateAsync"/> returns null (as if the persisted
    /// record vanished between the read and the write) and clears itself, so endpoints'
    /// `updated is null ? Results.Problem(...) : ...` branches can be exercised without a real
    /// database. Purely additive test-only infrastructure.
    /// </summary>
    public bool FailNextUpdate { get; set; }

    /// <summary>
    /// RA-482: when greater than zero, the next N calls to <see cref="UpdateIfOrsIdAbsentAsync"/>
    /// return null (as if a concurrent writer had already claimed that OrsId) and decrement this
    /// counter, so AddOverseasSite's retry-on-conflict loop can be exercised deterministically
    /// without real concurrency.
    /// </summary>
    public int FailNextOrsIdWrites { get; set; }

    public Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    )
    {
        if (application.Id is null || application.Id == ObjectId.Empty)
            application.Id = ObjectId.GenerateNewId();
        _store.Add(application);
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    // RA-516: mirrors AccreditationApplicationPersistence.GetByOrganisationAsync's server-side
    // newest-first sort, using the same shared AccreditationApplicationOrdering.NewestFirst() rule
    // the real class's compound index now enforces, so this fake stays a faithful stand-in.
    public Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId
    ) =>
        Task.FromResult<IEnumerable<AccreditationApplicationModel>>(
            _store.Where(a => a.OrganisationId == organisationId).NewestFirst().ToList()
        );

    public Task<AccreditationApplicationModel?> GetLiveByRegistrationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        var match = _store
            .Where(a =>
                a.OrganisationId == organisationId
                && a.RegistrationId == registrationId
                && a.MaterialType == materialType
                && a.Year == year
                && a.ApplicationStatus != ApplicationStatus.Withdrawn
            )
            .NewestFirst()
            .FirstOrDefault();
        return Task.FromResult(match is null ? null : ShallowCopy(match));
    }

    public Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(string registrationId)
    {
        IReadOnlyList<string> orsIds = _store
            .Where(a => a.RegistrationId == registrationId)
            .SelectMany(a => a.OverseasSites?.Sites ?? [])
            .Select(s => s.OrsId)
            .Where(id => id is not null)
            .Select(id => id!)
            .ToList();
        return Task.FromResult(orsIds);
    }

    public Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId
    )
    {
        if (!ObjectId.TryParse(applicationId, out var oid))
            return Task.FromResult<AccreditationApplicationModel?>(null);
        var stored = _store.FirstOrDefault(a => a.OrganisationId == organisationId && a.Id == oid);
        // Shallow copy so endpoint mutations to top-level value fields (ApplicationStatus, ApplicationReference, etc.)
        // don't affect the stored record unless UpdateAsync is explicitly called.
        return Task.FromResult(stored is null ? null : ShallowCopy(stored));
    }

    public Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(Guid workItemId)
    {
        var stored = _store.FirstOrDefault(a => a.CaseManagementWorkItemId == workItemId);
        return Task.FromResult(stored is null ? null : ShallowCopy(stored));
    }

    public Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application
    )
    {
        if (FailNextUpdate)
        {
            FailNextUpdate = false;
            return Task.FromResult<AccreditationApplicationModel?>(null);
        }

        var idx = _store.FindIndex(a => a.Id == application.Id);
        if (idx < 0)
            return Task.FromResult<AccreditationApplicationModel?>(null);
        // RA-516: mirrors AccreditationApplicationPersistence.ReplaceIfMatchAsync's optimistic
        // concurrency check.
        if (_store[idx].Version != application.Version)
            return Task.FromResult<AccreditationApplicationModel?>(null);
        application.Version++;
        _store[idx] = application;
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    public Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId
    )
    {
        if (FailNextOrsIdWrites > 0)
        {
            FailNextOrsIdWrites--;
            return Task.FromResult<AccreditationApplicationModel?>(null);
        }

        var idx = _store.FindIndex(a => a.Id == application.Id);
        if (idx < 0)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        var alreadyPresent = (_store[idx].OverseasSites?.Sites ?? []).Any(s => s.OrsId == orsId);
        if (alreadyPresent)
            return Task.FromResult<AccreditationApplicationModel?>(null);

        // RA-516: mirrors AccreditationApplicationPersistence.ReplaceIfMatchAsync's optimistic
        // concurrency check.
        if (_store[idx].Version != application.Version)
            return Task.FromResult<AccreditationApplicationModel?>(null);
        application.Version++;
        _store[idx] = application;
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    private static AccreditationApplicationModel ShallowCopy(AccreditationApplicationModel src) =>
        new()
        {
            Id = src.Id,
            OrganisationId = src.OrganisationId,
            // RA-503: was missing entirely - GetByIdAsync silently dropped OrgId on every read
            // through this fake, latent until GetById's backfill-and-persist logic needed to see
            // a previously-stored value survive a round trip through this test double.
            OrgId = src.OrgId,
            Year = src.Year,
            RegistrationId = src.RegistrationId,
            IsExporter = src.IsExporter,
            MaterialType = src.MaterialType,
            // RA-448: another pre-existing gap - GlassRecyclingProcess was silently
            // dropped on every read through this fake, latent until a Glass-typed
            // application needed it back (RegulatoryNumberGenerator requires it).
            GlassRecyclingProcess = src.GlassRecyclingProcess,
            ApplicationStatus = src.ApplicationStatus,
            SourceReExAccreditationId = src.SourceReExAccreditationId,
            SourceYear = src.SourceYear,
            ApplicationReference = src.ApplicationReference,
            CaseManagementReference = src.CaseManagementReference,
            CaseManagementWorkItemId = src.CaseManagementWorkItemId,
            // RA-448: was missing entirely before this feature - GetByIdAsync/
            // GetByCaseManagementWorkItemIdAsync silently dropped RegistrationReference
            // on every read through this fake.
            RegistrationReference = src.RegistrationReference,
            // .ToList() genuinely copies the list - assigning the reference directly
            // would alias the stored record's own list, contradicting this method's
            // isolation contract (mutating a "copy" via .Add() would corrupt the
            // persisted record directly, and every GetByIdAsync caller would share
            // one non-thread-safe List instance).
            PreviousRegistrationNumbers = src.PreviousRegistrationNumbers.ToList(),
            AccreditationReference = src.AccreditationReference,
            PreviousAccreditationNumbers = src.PreviousAccreditationNumbers.ToList(),
            SubmittedBy = src.SubmittedBy,
            WithdrawalReason = src.WithdrawalReason,
            DateSent = src.DateSent,
            CaseManagementStatusUpdatedAt = src.CaseManagementStatusUpdatedAt,
            DateLastEdited = src.DateLastEdited,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt,
            // RA-516: was missing, every read through this fake would silently reset the
            // concurrency token to 0, making every second update through it look like a version
            // conflict against the (already-incremented) stored record.
            Version = src.Version,
            Prns = src.Prns,
            BusinessPlan = src.BusinessPlan,
            SamplingPlan = src.SamplingPlan,
            // RA-482: was aliasing the stored record's own OverseasSites/Sites list, same
            // isolation gap PreviousRegistrationNumbers/PreviousAccreditationNumbers were already
            // fixed for above. Latent until UpdateIfOrsIdAbsentAsync started comparing the
            // "stored" copy against a site the endpoint had already Add()-ed to what turned out to
            // be the very same list instance -- a self-collision false positive on every write.
            OverseasSites = src.OverseasSites is null
                ? null
                : new AccreditationApplicationOverseasSites
                {
                    Sites = src.OverseasSites.Sites.ToList(),
                    SectionStatus = src.OverseasSites.SectionStatus,
                    Versions = src.OverseasSites.Versions.ToList(),
                },
            BesEvidence = src.BesEvidence,
            Query = src.Query,
        };
}
