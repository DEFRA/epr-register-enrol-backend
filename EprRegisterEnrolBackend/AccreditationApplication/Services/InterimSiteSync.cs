using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Keeps <see cref="OverseasSiteModel.InterimSite"/> and
/// <see cref="OverseasSiteModel.InterimSites"/> consistent (RA-603).
///
/// The list is authoritative; the singular field is a mirror kept for consumers that have not
/// moved over yet. Two rules, and every caller that mutates interim sites owes both of them:
///
/// <list type="bullet">
/// <item><see cref="Normalise"/> on the way in, so a document written before RA-603 - which has
/// only the singular field - is read as a one-element list and the rest of the code never has to
/// care which shape it came from.</item>
/// <item><see cref="SyncMirror"/> on the way out, so the mirror still describes the list after a
/// create, edit, withdraw or restore.</item>
/// </list>
///
/// The mirror points at the first site the operator has NOT withdrawn, not simply at index 0.
/// Withdrawn sites stay in the list on purpose (AC05 keeps them for reporting), so a mirror
/// defined as <c>InterimSites[0]</c> would sooner or later hand a legacy consumer a site the
/// operator had removed - which is worse than showing nothing, because it looks current.
/// </summary>
public static class InterimSiteSync
{
    /// <summary>
    /// Promotes a pre-RA-603 singular <see cref="OverseasSiteModel.InterimSite"/> into a
    /// one-element <see cref="OverseasSiteModel.InterimSites"/>.
    ///
    /// The list wins whenever it has anything in it: once a document has been written in the new
    /// shape the mirror is a derived copy, and re-appending it here would duplicate the first
    /// entry on every read. Idempotent, and a no-op for a site with no interim site at all.
    ///
    /// <c>IsNewSite</c> carries over exactly as persisted - nothing re-derives it - and
    /// <c>CreatedAt</c> is deliberately left null rather than stamped with "now": these documents
    /// genuinely predate the field, and a migration that invents audit timestamps is worse than
    /// one that admits it does not know.
    /// </summary>
    public static void Normalise(OverseasSiteModel site)
    {
        if (site.InterimSites.Count > 0)
            return;

        if (site.InterimSite is not null)
            site.InterimSites = [site.InterimSite];
    }

    /// <summary>
    /// Re-points <see cref="OverseasSiteModel.InterimSite"/> at the first non-withdrawn entry in
    /// the list, or null when every entry is withdrawn (or there are none). Call after any
    /// mutation of the list.
    ///
    /// Reads <see cref="OverseasSiteModel.InterimSites"/> directly and deliberately NOT through
    /// <see cref="Active"/>: this is a write-path operation, where the list is authoritative and
    /// the mirror is the thing being derived. Going through Active's legacy fallback would make
    /// the mirror derive from itself, so an empty list could never clear it and a withdrawn
    /// interim site would be resurrected by the very call meant to retire it. A write path that
    /// needs the legacy value promoted calls <see cref="Normalise"/> first.
    /// </summary>
    public static void SyncMirror(OverseasSiteModel site) =>
        site.InterimSite = site.InterimSites.FirstOrDefault(i => i.RemovedAt is null);

    /// <summary>
    /// Every interim site on this ORS, withdrawn ones included, in list order.
    ///
    /// Falls back to the singular mirror when the list is empty, which is what makes reading
    /// legacy-safe without a stored migration: RA-603 ships no backfill, so a document written
    /// before it keeps only <see cref="OverseasSiteModel.InterimSite"/> until something saves it
    /// again, and this returns the right answer for both shapes.
    ///
    /// That fallback lives here rather than in a read-path call to <see cref="Normalise"/> on
    /// purpose. The endpoints run against <c>FakeAccreditationApplicationPersistence</c> in tests
    /// and the Mongo class in production, so a normalise bolted onto the real persistence would be
    /// invisible to every endpoint test - the one shape of bug most likely to reach an environment
    /// unnoticed. Making the read itself correct cannot be forgotten by a caller and cannot
    /// diverge between the two.
    ///
    /// Pure: unlike <see cref="Normalise"/> it does not touch the site.
    /// </summary>
    public static IEnumerable<InterimSiteModel> All(OverseasSiteModel site)
    {
        if (site.InterimSites.Count > 0)
            return site.InterimSites;

        return site.InterimSite is null ? [] : [site.InterimSite];
    }

    /// <summary>
    /// The interim sites the operator currently has on this ORS, in list order, excluding any
    /// that have been withdrawn. This is what every display and validation path wants; the raw
    /// list is only for persistence and reporting.
    /// </summary>
    public static IEnumerable<InterimSiteModel> Active(OverseasSiteModel site) =>
        All(site).Where(i => i.RemovedAt is null);
}
