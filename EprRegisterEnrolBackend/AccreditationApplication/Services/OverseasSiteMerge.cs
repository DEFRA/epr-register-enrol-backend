using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Protects the server-owned fields on an overseas site from a wholesale client-supplied
/// replacement (RA-292 AC01/AC02).
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
                site.IsNewSite = knownSite.IsNewSite;

                // The promote/revert undo stack is [JsonIgnore], so it is never sent to the
                // frontend and can never come back on a PATCH. Carrying it across keeps a save of
                // the site list from silently destroying a promoted site's revert target.
                site.PreviousSites = knownSite.PreviousSites;
            }
            else
            {
                site.IsNewSite = true;
            }

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
