using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

/// <summary>
/// The single definition of "newest first" for accreditation applications.
/// </summary>
internal static class AccreditationApplicationOrdering
{
    // RA-357 made (organisationId, registrationId, materialType, year) a one-to-many key: a restart
    // after a withdrawal adds a second record for the same key. Both the Seed idempotency lookup
    // and the GetList response therefore need a stable order, and they must use the SAME one — the
    // frontend applies this rule too, so a divergence would mean backend and frontend disagreeing
    // about which record is "the live one".
    //
    // RA-516: production code (GetByOrganisationAsync, GetLiveByRegistrationAsync) now applies
    // this exact same order server-side, backed by the OrganisationId+CreatedAt+Id compound index
    // on AccreditationApplicationPersistence.DefineIndexes — an unindexed Mongo sort throws once
    // the result set exceeds the 32MB in-memory sort limit, which is why this used to be done here
    // in memory instead. This extension now only backs FakeAccreditationApplicationPersistence (the
    // endpoint test double), so its in-memory behaviour still matches the real, index-backed
    // server-side sort exactly.
    //
    // CreatedAt descending, then Id descending as a tiebreak. Id is an ObjectId, whose ordering is
    // byte-wise (timestamp, then random, then counter), which matches a plain lexicographic
    // comparison of the 24-char lowercase hex `applicationId` the frontend sorts on.
    internal static IOrderedEnumerable<AccreditationApplicationModel> NewestFirst(
        this IEnumerable<AccreditationApplicationModel> applications
    ) => applications.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id);
}
