using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Driver;

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

    /// <summary>
    /// RA-519: targeted (field-level, `$set`/`$push`) update filtered only by `_id`, as opposed to
    /// <see cref="UpdateAsync"/>'s whole-document replace. Two concurrent writers that each touch
    /// disjoint fields via this method (or one via this method and one via
    /// StatusChangedFromCaseManagement's whole-document replace touching different fields) no
    /// longer clobber each other — a targeted update only ever overwrites the specific fields
    /// named in <paramref name="update"/>, so it can never lose a concurrent writer's unrelated
    /// change the way two whole-document replaces filtered only by `_id` can. Stamps
    /// <c>UpdatedAt</c> and increments <c>Version</c> as part of the same atomic update. Returns
    /// the updated document, or <c>null</c> if no document with <paramref name="id"/> exists.
    /// </summary>
    Task<AccreditationApplicationModel?> UpdateFieldsAsync(
        ObjectId id,
        UpdateDefinition<AccreditationApplicationModel> update,
        CancellationToken cancellationToken = default
    );
}
