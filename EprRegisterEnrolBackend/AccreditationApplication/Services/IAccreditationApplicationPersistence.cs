using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public interface IAccreditationApplicationPersistence
{
    Task<AccreditationApplicationModel?> CreateAsync(AccreditationApplicationModel application);
    Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(string organisationId);
    Task<AccreditationApplicationModel?> GetByIdAsync(string organisationId, string applicationId);
    Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(Guid workItemId);
    Task<AccreditationApplicationModel?> UpdateAsync(AccreditationApplicationModel application);
}
