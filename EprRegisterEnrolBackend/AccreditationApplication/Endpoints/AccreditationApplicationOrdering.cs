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
    // Deliberately an in-memory sort, NOT a Mongo-side Sort(): index creation is disabled
    // service-wide (MongoService.EnsureIndexes has Collection.Indexes.CreateMany commented out —
    // see epr-hsjp), so a server-side sort on CreatedAt would be unindexed and would throw once a
    // result set exceeded Mongo's 32MB in-memory sort limit. Do not "optimise" this down into
    // AccreditationApplicationPersistence until epr-hsjp is fixed and a supporting index exists.
    //
    // CreatedAt descending, then Id descending as a tiebreak. Id is an ObjectId, whose ordering is
    // byte-wise (timestamp, then random, then counter), which matches a plain lexicographic
    // comparison of the 24-char lowercase hex `applicationId` the frontend sorts on.
    internal static IOrderedEnumerable<AccreditationApplicationModel> NewestFirst(
        this IEnumerable<AccreditationApplicationModel> applications
    ) => applications.OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id);
}
