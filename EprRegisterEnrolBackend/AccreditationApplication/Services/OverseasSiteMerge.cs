using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Protects the server-owned fields on an overseas site from a wholesale client-supplied
/// replacement (RA-292 AC01/AC02, plus epr-zgrb).
///
/// Four fields are restored from the persisted site: <see cref="OverseasSiteModel.IsNewSite"/>
/// (and its interim-site counterpart), <see cref="OverseasSiteModel.RegisteredNowAccredited"/>,
/// <see cref="OverseasSiteModel.PreviousSites"/>, and <see cref="OverseasSiteModel.OrsId"/>.
///
/// They differ only in how exposed they were, not in principle. `PreviousSites` could never
/// survive a round-trip at all, being `[JsonIgnore]`. `RegisteredNowAccredited` and `OrsId`
/// survived precisely as long as the client happened to echo them back. `IsNewSite` had a
/// defaulting hazard on top. The rule is one rule: if the server owns the field, the server
/// derives it — because "the client currently happens to preserve it" is not a guarantee, and
/// each of these was found the hard way.
///
/// <c>PATCH .../overseas-sites</c> replaces the whole site list with the request body, so any
/// field the client does not echo back is lost. That is harmless for operator-entered data — the
/// operator owns it — but <see cref="OverseasSiteModel.IsNewSite"/> now drives the "new" badge a
/// regulator uses to decide what needs validating, so it must be derived here rather than
/// accepted from a client, exactly as <see cref="PrnsAuthoriserMerge"/> does for authority-to-issue
/// contacts. Without this, a PATCH that simply omits <c>isNewSite</c> would flip every site to new
/// — including registered sites that ReEx correctly marked as not new — which is indistinguishable
/// from the badge being broken.
/// </summary>
public static class OverseasSiteMerge
{
    /// <summary>
    /// Returns the incoming sites with the server-owned fields restored from
    /// <paramref name="persisted"/>, matching on <c>SiteId</c> (the stable key; ORS and interim
    /// ids come from one shared sequence, but they are looked up separately so the two id spaces
    /// can never cross-contaminate).
    ///
    /// A known <c>SiteId</c> keeps the persisted value and the client's is discarded. An unknown
    /// one is treated as new, mirroring the unknown-email rule for authorisers — though sites are
    /// only ever created through the dedicated add-site endpoints, so an unknown id arriving on a
    /// PATCH is anomalous in the first place.
    ///
    /// The incoming list replaces the persisted one wholesale: a site the client omitted is
    /// genuinely dropped, never resurrected.
    /// </summary>
    public static List<OverseasSiteModel> Merge(
        IEnumerable<OverseasSiteModel>? persisted,
        IEnumerable<OverseasSiteModel>? incoming
    )
    {
        if (incoming is null)
            return [];

        var persistedSites = new Dictionary<int, OverseasSiteModel>();
        var persistedInterimIsNew = new Dictionary<int, bool>();
        foreach (var site in persisted ?? [])
        {
            // First entry wins if the persisted list somehow holds the same id twice.
            persistedSites.TryAdd(site.SiteId, site);
            if (site.InterimSite is not null)
            {
                persistedInterimIsNew.TryAdd(site.InterimSite.SiteId, site.InterimSite.IsNewSite);
            }
        }

        // Deliberately mutates the incoming instances rather than rebuilding them. These come
        // straight from model binding and are not shared, and a field-by-field clone would be one
        // more place to forget a field when the model grows — the very failure this ticket exists
        // to fix.
        var merged = incoming.ToList();
        foreach (var site in merged)
        {
            if (persistedSites.TryGetValue(site.SiteId, out var knownSite))
            {
                // Fixed at creation and never changed by any journey: AddOverseasSite sets it,
                // PromoteOverseasSiteRequest has no OrsId field, ApplyPromotedFields doesn't touch
                // it, and RestoreSnapshotFields doesn't restore it. So a PATCH altering it is
                // always wrong.
                //
                // This one is load-bearing beyond the operator journey. A null OrsId is the marker
                // that a site came from ReEx rather than the operator (HttpReExApiAdapter has
                // never set it), which is the discriminator the epr-2uxy remediation uses to tell
                // a defaulted isNewSite=true from a genuine one. Leaving it clobberable would mean
                // the remediation's correctness rested on the same accidental round-trip this
                // whole story exists to eliminate. Restoring it also protects the OrsId-uniqueness
                // invariant that AddOverseasSite enforces at line ~570.
                site.OrsId = knownSite.OrsId;

                site.IsNewSite = knownSite.IsNewSite;

                // Set only by PromoteOverseasSite/RevertOverseasSite, never by the operator. It is
                // serialised (unlike PreviousSites below), so it round-trips whenever the client
                // happens to echo it back — but a body that simply omits the key deserialises to
                // false and silently un-promotes the site, after which revert fails on the
                // promote-flag guard. Deriving it here removes that dependence on the client.
                site.RegisteredNowAccredited = knownSite.RegisteredNowAccredited;

                // The promote/revert undo stack is [JsonIgnore], so it is never sent to the
                // frontend and can never come back on a PATCH. Carrying it across keeps a save of
                // the site list from silently destroying a promoted site's revert target.
                site.PreviousSites = knownSite.PreviousSites;
            }
            else
            {
                // Same rule as the known branch — derive from persisted state — which lands on the
                // opposite literal because the ground state of each concept differs. A site the
                // server has never seen is by definition new, and equally cannot have been
                // promoted: promotion only ever happens via PromoteOverseasSite against a site
                // that is already persisted.
                site.IsNewSite = true;
                site.RegisteredNowAccredited = false;

                // OrsId is deliberately left as supplied here. There is no persisted value to
                // restore, and forcing it to null would not only destroy data but would make the
                // site masquerade as ReEx-sourced under the epr-2uxy discriminator — turning a
                // defensive measure into the exact false-negative it exists to prevent.
            }

            // RA-486: InterimSiteModel.OperationCodes is an operator-entered field, like SiteName/
            // Address above - it needs no restoring here and carries through untouched as part of
            // the incoming InterimSite object. The `is not null` guard below is also what makes a
            // PATCH body with InterimSite: null genuinely clear an existing interim site: nothing
            // downstream of this method re-populates it, so an incoming null stays null on the
            // merged site with no side effects on any of its other fields.
            if (site.InterimSite is not null)
            {
                site.InterimSite.IsNewSite =
                    !persistedInterimIsNew.TryGetValue(
                        site.InterimSite.SiteId,
                        out var interimPreviouslyNew
                    ) || interimPreviouslyNew;
            }
        }

        return merged;
    }
}
