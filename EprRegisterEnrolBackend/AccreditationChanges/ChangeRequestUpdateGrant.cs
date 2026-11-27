using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Status;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-782: grants an update-ORS change request item: the details the operator changed, their interim site changes and
/// any new BES evidence are written onto the accredited ORS, on the legacy accreditation application and in the domain
/// model, inside the dual-write unit of work with the item's own write. The ORS stays accredited as it was: its
/// selection, date and status are not touched.
/// </summary>
/// <remarks>
/// Safe to run again: details are applied by what the update changed, BES files by id, a withdrawal only once, and an
/// interim site the update adds is found by the id the grant recorded on its change (or, after a lost item write
/// without a transaction, by having been added at this decision's time with these details).
/// </remarks>
internal static class ChangeRequestUpdateGrant
{
    // A legacy interim site the grant changed, added or withdrew, and the status it has in the model now.
    private sealed record GrantedInterim(InterimSiteModel Interim, InterimSiteStatus Status);

    /// <exception cref="ChangeRequestWriteConflictException">The legacy application changed since it was read.</exception>
    /// <exception cref="InvalidOperationException">The domain model lacks the application or ORS.</exception>
    public static async Task<IResult?> ApplyAsync(
        AccreditationChangeRequest changeRequest,
        ChangeRequestSite item,
        DateTime decidedAt,
        IAccreditationApplicationPersistence applications,
        IServiceProvider services,
        IClientSessionHandle? session,
        CancellationToken cancellationToken
    )
    {
        if (changeRequest.ApplicationId is null)
            return Results.Conflict("The change request has no accreditation to grant it on.");

        var application = await applications.GetByIdAsync(
            changeRequest.OrganisationId,
            changeRequest.ApplicationId,
            session
        );
        var site = application?.OverseasSites?.Sites.Find(s => s.SiteId == item.SiteId && s.OrsId == item.OrsId);
        if (application is null || site is null || !site.Selected)
            return Results.Conflict(
                "The overseas site is no longer accredited on the accreditation, so its update cannot be granted."
            );

        InterimSiteSync.Normalise(site);
        if (RefuseInterimChanges(site, item) is { } refused)
            return refused;

        var before = site.ToBsonDocument();
        ApplyDetails(site, UpdateOverseasSiteRules.GrantedDetails(OverseasSiteDetails.From(site), item));
        var (capacityError, interims) = await ApplyInterimChangesAsync(
            new LegacyTarget(application, site, applications, services, session),
            item,
            decidedAt
        );
        if (capacityError is not null)
            return capacityError;
        ChangeRequestGrant.AddBesEvidence(site, item.BesEvidence);
        InterimSiteSync.SyncMirror(site);

        if (!site.ToBsonDocument().Equals(before) && await applications.UpdateAsync(application, session) is null)
            throw new ChangeRequestWriteConflictException();

        // As for an add: without dual-write there is no session and no model to write. The model is written here, in
        // the work, so the unit of work's sync finds the interim sites already Active rather than recording a new one
        // as Proposed.
        if (session is not null)
        {
            await GrantInModelAsync(
                services.GetRequiredService<ApplicationGroupStores>(),
                application.Id!.Value,
                site,
                item,
                interims,
                session,
                cancellationToken
            );
        }

        return null;
    }

    // Every interim site change is checked before anything is applied, so a refusal writes nothing.
    private static IResult? RefuseInterimChanges(OverseasSiteModel site, ChangeRequestSite item)
    {
        foreach (var change in item.InterimSiteChanges)
        {
            var refused = change.Action switch
            {
                InterimSiteChangeAction.Change => change.InterimSiteId is not { } id
                    || change.Proposed is null
                    || UpdateOverseasSiteRules.ActiveInterimSite(site, id) is null,
                InterimSiteChangeAction.Remove => change.InterimSiteId is not { } id
                    || !InterimSiteSync.All(site).Any(i => i.SiteId == id),
                InterimSiteChangeAction.Add => change.Proposed is null,
                _ => true,
            };
            if (refused)
                return Results.Conflict(
                    $"Interim site '{change.InterimSiteId}' is no longer on the overseas site as the update expects, so the update cannot be granted."
                );
        }
        return null;
    }

    /// <summary>
    /// The update's details written onto the legacy site. The free-text address the site keeps is rebuilt from its
    /// parts as the operator's own ORS journey builds it, so it doesn't go on showing the old address.
    /// </summary>
    private static void ApplyDetails(OverseasSiteModel site, OverseasSiteDetails details)
    {
        var addressChanged = site.AddressLine1 != details.AddressLine1 || site.TownOrCity != details.TownOrCity;
        site.SiteName = details.SiteName ?? site.SiteName;
        site.AddressLine1 = details.AddressLine1;
        site.AddressLine2 = details.AddressLine2;
        site.TownOrCity = details.TownOrCity;
        site.ContactName = details.ContactName;
        site.ContactEmail = details.ContactEmail;
        site.ContactPhone = details.ContactPhone;
        site.RepatriatedLoads = details.RepatriatedLoads;
        if (addressChanged && !string.IsNullOrWhiteSpace(site.SiteAddress))
            site.SiteAddress = FormatAddress(site.AddressLine1, site.TownOrCity, site.Country);
    }

    private static string FormatAddress(params string?[] parts) =>
        string.Join(", ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    private sealed record LegacyTarget(
        AccreditationApplicationModel Application,
        OverseasSiteModel Site,
        IAccreditationApplicationPersistence Applications,
        IServiceProvider Services,
        IClientSessionHandle? Session
    );

    private static async Task<(IResult? Error, List<GrantedInterim> Interims)> ApplyInterimChangesAsync(
        LegacyTarget target,
        ChangeRequestSite item,
        DateTime decidedAt
    )
    {
        var site = target.Site;
        var granted = new List<GrantedInterim>();
        List<string?>? numbersTaken = null;
        foreach (var change in item.InterimSiteChanges)
        {
            switch (change.Action)
            {
                case InterimSiteChangeAction.Change:
                    var changed = UpdateOverseasSiteRules.ActiveInterimSite(site, change.InterimSiteId!.Value)!;
                    CopyDetails(changed, change.Proposed!);
                    granted.Add(new GrantedInterim(changed, InterimSiteStatus.Active));
                    break;

                case InterimSiteChangeAction.Remove:
                    var removed = InterimSiteSync.All(site).First(i => i.SiteId == change.InterimSiteId);
                    InterimSiteStatusHistory.Withdraw(removed, decidedAt);
                    granted.Add(new GrantedInterim(removed, InterimSiteStatus.Withdrawn));
                    break;

                case InterimSiteChangeAction.Add:
                    var added = AddedBefore(site, change, decidedAt, granted);
                    if (added is null)
                    {
                        numbersTaken ??=
                        [
                            .. await SiteIdAllocation.InterimSiteNumberScopeAsync(
                                target.Applications,
                                target.Application,
                                target.Services.GetRequiredService<IModelInterimSiteNumbers>(),
                                target.Session
                            ),
                        ];
                        var number = OrsIdGenerator.GenerateNext(numbersTaken);
                        if (number.CapacityExceeded)
                            return (
                                Results.Conflict(
                                    "This registration has reached the maximum of 999 interim sites, so the update cannot be granted."
                                ),
                                []
                            );
                        added = SiteIdAllocation.BuildInterimSite(
                            SiteIdAllocation.NextSiteId(target.Application.OverseasSites!),
                            number.OrsId!,
                            change.Proposed!,
                            decidedAt
                        );
                        site.InterimSites.Add(added);
                        numbersTaken.Add(added.SiteNumber);
                    }
                    change.InterimSiteId = added.SiteId;
                    granted.Add(new GrantedInterim(added, InterimSiteStatus.Active));
                    break;
            }
        }
        return (null, granted);
    }

    // The interim site this add already put on the ORS: by the id the grant recorded on the change, or, when a lost
    // item write took that record with it (only without a transaction), by its having been added at this decision's
    // time with these details.
    private static InterimSiteModel? AddedBefore(
        OverseasSiteModel site,
        InterimSiteChange change,
        DateTime decidedAt,
        List<GrantedInterim> granted
    )
    {
        var claimed = granted.Select(g => g.Interim.SiteId).ToHashSet();
        var candidates = InterimSiteSync.All(site).Where(i => !claimed.Contains(i.SiteId));
        return change.InterimSiteId is { } id
            ? candidates.FirstOrDefault(i => i.SiteId == id)
            : candidates.FirstOrDefault(i =>
                i.IsNewSite && i.CreatedAt == decidedAt && InterimSiteDetails.From(i) == change.Proposed
            );
    }

    // An interim site keeps its own country and recycling operations (RA-682): only what the operator may change.
    private static void CopyDetails(InterimSiteModel interim, InterimSiteDetails proposed)
    {
        interim.SiteName = proposed.SiteName;
        interim.AddressLine1 = proposed.AddressLine1;
        interim.AddressLine2 = proposed.AddressLine2;
        interim.TownOrCity = proposed.TownOrCity;
        interim.StateOrRegion = proposed.StateOrRegion;
        interim.Postcode = proposed.Postcode;
        interim.ContactName = proposed.ContactName;
        interim.ContactEmail = proposed.ContactEmail;
        interim.ContactPhone = proposed.ContactPhone;
    }

    private static async Task GrantInModelAsync(
        ApplicationGroupStores stores,
        ObjectId applicationId,
        OverseasSiteModel site,
        ChangeRequestSite item,
        List<GrantedInterim> interims,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var (entity, ors) = await ChangeRequestGrant.FindInModelAsync(
            stores,
            applicationId,
            site.OrsId,
            session,
            cancellationToken
        );
        var change = ChangeInfo.Of(ChangeReason.ChangeRequestGranted, applicationId, ors.OrsId);

        // One ORS version for the details and every interim site; an update that changes nothing writes nothing, so
        // a replay is a no-op.
        ApplyDetails(ors, UpdateOverseasSiteRules.GrantedDetails(Details(ors), item));
        foreach (var (interim, status) in interims)
            ApplyInterim(ors, interim, status);
        await stores.OverseasSites.Versions.UpdateAsync(ors, ors.Version, change, session, cancellationToken);

        // The new BES files join the ORS's entry on the Accreditation, as the legacy site's: the entry is what the
        // accreditation serves as the site's evidence. Not a new entry: the ORS is already accredited.
        var files = LegacyApplicationMapper.ToBesEvidence(item.BesEvidence)?.BesEvidenceUploads ?? [];
        if (files.Count == 0)
            return;
        var accreditation = await ChangeRequestGrant.FindAccreditationAsync(stores, entity, session, cancellationToken);
        var entry = accreditation?.AccreditationOverseasSites.Find(e => e.OverseasSiteId == ors.EntityId);
        if (accreditation is null || entry is null)
            return;
        var held = entry.BesEvidence.Select(f => f.FileId).ToHashSet();
        var added = files.Where(f => !held.Contains(f.FileId)).ToList();
        if (added.Count == 0)
            return;
        entry.BesEvidence.AddRange(added);
        await stores.Accreditations.Versions.UpdateAsync(
            accreditation,
            accreditation.Version,
            change,
            session,
            cancellationToken
        );
    }

    private static OverseasSiteDetails Details(OverseasSiteEntity ors) =>
        new()
        {
            SiteName = ors.SiteName,
            AddressLine1 = ors.Address?.Line1,
            AddressLine2 = ors.Address?.Line2,
            TownOrCity = ors.Address?.TownOrCity,
            ContactName = ors.Contact?.FullName,
            ContactEmail = ors.Contact?.Email,
            ContactPhone = ors.Contact?.Phone,
            RepatriatedLoads = ors.RepatriatedLoads,
        };

    private static void ApplyDetails(OverseasSiteEntity ors, OverseasSiteDetails details)
    {
        if (details == Details(ors))
            return;

        var addressChanged =
            ors.Address?.Line1 != details.AddressLine1 || ors.Address?.TownOrCity != details.TownOrCity;
        ors.SiteName = details.SiteName;
        ors.Address ??= new PostalAddress { Country = ors.Country };
        ors.Address.Line1 = details.AddressLine1;
        ors.Address.Line2 = details.AddressLine2;
        ors.Address.TownOrCity = details.TownOrCity;
        ors.Contact ??= new ContactPerson();
        ors.Contact.FullName = details.ContactName;
        ors.Contact.Email = details.ContactEmail;
        ors.Contact.Phone = details.ContactPhone;
        ors.RepatriatedLoads = details.RepatriatedLoads;
        if (addressChanged && !string.IsNullOrWhiteSpace(ors.FormattedAddress))
            ors.FormattedAddress = FormatAddress(ors.Address.Line1, ors.Address.TownOrCity, ors.Country);
    }

    // The interim site as the legacy grant left it, under the number the model knows it by (the migration may have
    // renumbered it), with the status the grant gives it.
    private static void ApplyInterim(OverseasSiteEntity ors, InterimSiteModel interim, InterimSiteStatus status)
    {
        var stored = LegacySiteMapper.FindInterim(ors, interim.SiteNumber);
        var details = LegacySiteMapper.ToInterimSite(interim, stored?.SiteNumber ?? interim.SiteNumber);
        if (stored is null)
        {
            details.Status = status;
            ors.InterimSites.Add(details);
            return;
        }

        stored.SiteName = details.SiteName;
        stored.Address = details.Address;
        stored.Contact = details.Contact;
        stored.OperationCodes = details.OperationCodes;
        stored.RemovedAt = details.RemovedAt;
        stored.CreatedAt ??= details.CreatedAt;
        if (stored.Status != status)
        {
            OverseasSiteTransitions.Interim.EnsureCanTransition(stored.Status, status);
            stored.Status = status;
        }
    }
}
