using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public interface IAccreditationApplicationPersistence
{
    Task<AccreditationApplicationModel?> CreateAsync(AccreditationApplicationModel application);

    /// <summary>
    /// RA-516: sorted server-side, newest first (CreatedAt descending, then Id descending as a
    /// tiebreak) - backed by the OrganisationId+CreatedAt+Id compound index on
    /// AccreditationApplicationPersistence.DefineIndexes. Callers no longer need to re-sort.
    /// </summary>
    Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(string organisationId);

    /// <summary>
    /// RA-516: server-side equivalent of "GetByOrganisationAsync then filter by
    /// RegistrationId/MaterialType/Year, exclude Withdrawn, take the newest" - used by Seed's
    /// idempotency check. Returns null when nothing matches.
    /// </summary>
    Task<AccreditationApplicationModel?> GetLiveByRegistrationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    );

    /// <summary>
    /// RA-516: server-side equivalent of "GetByOrganisationAsync then filter by RegistrationId
    /// then flatten every OverseasSites.Sites.OrsId" - used to scope OrsId generation across a
    /// registration's applications. Skips sites with a null OrsId.
    /// </summary>
    Task<IReadOnlyList<string>> GetOrsIdsByRegistrationAsync(string registrationId);

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
