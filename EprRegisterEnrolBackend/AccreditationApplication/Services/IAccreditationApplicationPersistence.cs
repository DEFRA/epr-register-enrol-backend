using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public interface IAccreditationApplicationPersistence
{
    Task<AccreditationApplicationModel?> CreateAsync(AccreditationApplicationModel application);
    Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(string organisationId);
    Task<AccreditationApplicationModel?> GetByIdAsync(string organisationId, string applicationId);
    Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(Guid workItemId);
    Task<AccreditationApplicationModel?> UpdateAsync(AccreditationApplicationModel application);

    /// <summary>
    /// Same whole-document replace contract as <see cref="UpdateAsync"/>, but the write only
    /// succeeds if <paramref name="orsId"/> is not already present among the application's
    /// overseas sites at write time -- returns <c>null</c> (instead of persisting) if a
    /// concurrent writer already inserted it. RA-482: lets AddOverseasSite retry with a freshly
    /// computed id rather than risking a duplicate.
    /// </summary>
    Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId
    );
}
