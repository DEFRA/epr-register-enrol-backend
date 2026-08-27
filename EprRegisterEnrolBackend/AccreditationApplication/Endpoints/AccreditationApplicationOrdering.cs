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
    // about which record is "the live one". Defined once here so the two call sites cannot drift.
    //
    // Deliberately an in-memory sort, NOT a Mongo-side Sort(): AccreditationApplicationPersistence
    // does not define an index on CreatedAt, so a server-side sort on it would be unindexed, and
    // an unindexed sort throws once the result set exceeds Mongo's 32MB in-memory sort limit.
    // MongoService.EnsureIndexes now does create the indexes each subclass defines (via
    // MongoIndexReconciler) — do not "optimise" this down into AccreditationApplicationPersistence
    // until a supporting index on CreatedAt is actually added to its DefineIndexes.
    //
    // CreatedAt descending, then Id descending as a tiebreak. Id is an ObjectId, whose ordering is
    // byte-wise (timestamp, then random, then counter), which matches a plain lexicographic
    // comparison of the 24-char lowercase hex `applicationId` the frontend sorts on.
    internal static IOrderedEnumerable<AccreditationApplicationModel> NewestFirst(
        this IEnumerable<AccreditationApplicationModel> applications
    ) => applications.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id);
}
