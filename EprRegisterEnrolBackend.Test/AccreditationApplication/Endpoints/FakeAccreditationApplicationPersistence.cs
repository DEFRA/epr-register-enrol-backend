using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class FakeAccreditationApplicationPersistence : IAccreditationApplicationPersistence
{
    private readonly List<AccreditationApplicationModel> _store = [];

    public void Seed(AccreditationApplicationModel application) => _store.Add(application);

    public void Clear() => _store.Clear();

    public Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    )
    {
        if (application.Id is null || application.Id == ObjectId.Empty)
            application.Id = ObjectId.GenerateNewId();
        _store.Add(application);
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    public Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId
    ) =>
        Task.FromResult<IEnumerable<AccreditationApplicationModel>>(
            _store.Where(a => a.OrganisationId == organisationId).ToList()
        );

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

    public Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application
    )
    {
        var idx = _store.FindIndex(a => a.Id == application.Id);
        if (idx < 0)
            return Task.FromResult<AccreditationApplicationModel?>(null);
        _store[idx] = application;
        return Task.FromResult<AccreditationApplicationModel?>(application);
    }

    private static AccreditationApplicationModel ShallowCopy(AccreditationApplicationModel src) =>
        new()
        {
            Id = src.Id,
            OrganisationId = src.OrganisationId,
            Year = src.Year,
            RegistrationId = src.RegistrationId,
            MaterialType = src.MaterialType,
            ApplicationStatus = src.ApplicationStatus,
            SourceReExAccreditationId = src.SourceReExAccreditationId,
            SourceYear = src.SourceYear,
            ApplicationReference = src.ApplicationReference,
            CaseManagementReference = src.CaseManagementReference,
            CaseManagementWorkItemId = src.CaseManagementWorkItemId,
            SubmittedBy = src.SubmittedBy,
            DateSent = src.DateSent,
            DateLastEdited = src.DateLastEdited,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt,
            Prns = src.Prns,
            BusinessPlan = src.BusinessPlan,
            SamplingPlan = src.SamplingPlan,
        };
}
