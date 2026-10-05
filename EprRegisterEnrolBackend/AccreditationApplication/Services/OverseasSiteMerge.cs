using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Restores the server-owned fields on overseas sites after <c>PATCH .../overseas-sites</c>,
/// which replaces the whole site list with the request body (RA-292 AC01/AC02, epr-zgrb).
///
/// The rule: if the server owns a field, the server derives it. Restored from the persisted site:
/// <see cref="OverseasSiteModel.OrsId"/>, <see cref="OverseasSiteModel.IsNewSite"/>,
/// <see cref="OverseasSiteModel.RegisteredNowAccredited"/> and
/// <see cref="OverseasSiteModel.PreviousSites"/>; and on each interim site, IsNewSite, CreatedAt,
/// RemovedAt and (when the client sends none) OperationCodes. Same approach as
/// <see cref="PrnsAuthoriserMerge"/>.
/// </summary>
public static class OverseasSiteMerge
{
    /// <summary>
    /// Returns the incoming sites with server-owned fields restored from
    /// <paramref name="persisted"/>, matched on <c>SiteId</c>. ORS and interim ids are looked up
    /// separately so the two can never be confused. An unknown id is treated as new.
    ///
    /// ORS sites the client omits are dropped. Interim sites are never dropped: see
    /// <see cref="ReattachOmittedInterimSites"/>.
    /// </summary>
    public static List<OverseasSiteModel> Merge(
        IEnumerable<OverseasSiteModel>? persisted,
        IEnumerable<OverseasSiteModel>? incoming
    )
    {
        if (incoming is null)
            return [];

        var persistedSites = new Dictionary<int, OverseasSiteModel>();
        var persistedInterimSites = new Dictionary<int, InterimSiteModel>();
        // RA-603: which interim sites each persisted ORS holds, so an omitted one can be told
        // apart from one it never had.
        var persistedInterimSitesByParent = new Dictionary<int, List<InterimSiteModel>>();
        foreach (var site in persisted ?? [])
        {
            // First entry wins if the persisted list somehow holds the same id twice.
            persistedSites.TryAdd(site.SiteId, site);

            // A pre-RA-603 document carries only the singular field; read it as a list.
            InterimSiteSync.Normalise(site);
            persistedInterimSitesByParent.TryAdd(site.SiteId, site.InterimSites);
            foreach (var interim in site.InterimSites)
            {
                persistedInterimSites.TryAdd(interim.SiteId, interim);
            }
        }

        // Mutates the incoming instances rather than cloning them: they come straight from model
        // binding and are not shared, and a field-by-field clone is one more place to miss a field.
        var merged = incoming.ToList();
        foreach (var site in merged)
        {
            if (persistedSites.TryGetValue(site.SiteId, out var knownSite))
            {
                // Fixed at creation and never changed by any journey, and the key the OrsId
                // uniqueness guard relies on - so a PATCH altering it is always wrong.
                site.OrsId = knownSite.OrsId;

                site.IsNewSite = knownSite.IsNewSite;

                // Set only by promote/revert. A body omitting it would deserialise to false and
                // silently un-promote the site.
                site.RegisteredNowAccredited = knownSite.RegisteredNowAccredited;

                // [JsonIgnore], so it can never come back on a PATCH; carrying it across keeps a
                // promoted site's revert target.
                site.PreviousSites = knownSite.PreviousSites;
            }
            else
            {
                // A site the server has never seen is new and cannot have been promoted. OrsId is
                // left as supplied - there is nothing persisted to restore.
                site.IsNewSite = true;
                site.RegisteredNowAccredited = false;
            }

            // RA-603: a client sending only the singular field is read as a one-element list.
            // When the list IS present it is authoritative and the singular field is ignored.
            InterimSiteSync.Normalise(site);

            RestoreServerOwnedInterimFields(site, persistedInterimSites);
            ReattachOmittedInterimSites(site, persistedInterimSitesByParent);

            // Re-point the legacy mirror at the first interim site still active.
            InterimSiteSync.SyncMirror(site);
        }

        return merged;
    }

    /// <summary>
    /// RA-603: restores the interim-site fields the server owns, so a PATCH cannot set them.
    /// </summary>
    private static void RestoreServerOwnedInterimFields(
        OverseasSiteModel site,
        Dictionary<int, InterimSiteModel> persistedInterimSites
    )
    {
        foreach (var interim in site.InterimSites)
        {
            var hasPersistedInterim = persistedInterimSites.TryGetValue(
                interim.SiteId,
                out var persistedInterim
            );

            interim.IsNewSite = !hasPersistedInterim || persistedInterim!.IsNewSite;

            // RA-486: OperationCodes defaults to [] so older documents still deserialise, which
            // means a body that omits it would otherwise wipe the persisted codes.
            if (interim.OperationCodes.Count == 0 && hasPersistedInterim)
            {
                interim.OperationCodes = persistedInterim!.OperationCodes;
            }

            // AC05: RemovedAt IS the withdrawal, so accepting it from a client would let a bulk
            // PATCH un-withdraw an interim site and bypass the restore endpoint.
            if (hasPersistedInterim)
            {
                interim.CreatedAt = persistedInterim!.CreatedAt;
                interim.RemovedAt = persistedInterim.RemovedAt;
            }
        }
    }

    /// <summary>
    /// RA-603 AC05: nothing here drops an interim site. The frontend leaves withdrawn ones out of
    /// what it sends, so a wholesale replace would erase the records AC05 keeps for reporting. An
    /// interim site the client omitted is reattached: as persisted if already withdrawn, otherwise
    /// withdrawn now rather than destroyed.
    ///
    /// For a pre-RA-603 client that sends only the singular field, <c>interimSite: null</c>
    /// therefore still withdraws the site. A body that also carries the list does not: the list is
    /// authoritative, and a site still in it stays active.
    /// </summary>
    private static void ReattachOmittedInterimSites(
        OverseasSiteModel site,
        Dictionary<int, List<InterimSiteModel>> persistedInterimSitesByParent
    )
    {
        if (!persistedInterimSitesByParent.TryGetValue(site.SiteId, out var persistedInterims))
            return;

        var sentIds = site.InterimSites.Select(i => i.SiteId).ToHashSet();
        foreach (var omitted in persistedInterims)
        {
            if (sentIds.Contains(omitted.SiteId))
                continue;

            omitted.RemovedAt ??= DateTime.UtcNow;
            site.InterimSites.Add(omitted);
        }
    }
}
