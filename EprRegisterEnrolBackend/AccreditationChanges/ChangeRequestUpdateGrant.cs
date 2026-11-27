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
/// without a transaction, by having been added at this decision's time with these details). A new interim site's
/// number is minted as the operator's own add mints one (<see cref="SiteIdAllocation"/>), through the guarded write.
/// </remarks>
internal static class ChangeRequestUpdateGrant
{
    // A legacy interim site the grant changed, added or withdrew, and the status it has in the model now.
    private sealed record GrantedInterim(InterimSiteModel Interim, InterimSiteStatus Status);

    /// <exception cref="ChangeRequestWriteConflictException">The legacy application changed since it was read.</exception>
    /// <exception cref="InvalidOperationException">
    /// The domain model lacks the application or ORS, or, with new BES files, the Accreditation or its ORS entry.
    /// </exception>
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

        // Mongo keeps dates to the millisecond and Case Management's occurredAt has finer ticks: an interim site added
        // at the untruncated time would never match it again once read back (AddedBefore).
        decidedAt = ToMilliseconds(decidedAt);

        var application = await applications.GetByIdAsync(
            changeRequest.OrganisationId,
            changeRequest.ApplicationId,
            session
        );
        if (application is null)
            return Results.Conflict(NotAccreditedMessage);
        if (RefuseGrant(application, item) is { } refused)
            return refused;

        // New interim sites are minted first, each through the guarded write, so no concurrent writer can take the
        // same regulator-visible number (RA-603). The rest of the update is then applied to what that wrote.
        var minted = await MintInterimSitesAsync(
            new LegacyTarget(changeRequest, applications, services, session),
            application,
            item,
            decidedAt
        );
        if (minted.Error is not null)
            return minted.Error;
        application = minted.Application;
        var site = AccreditedSite(application, item)!;

        var before = site.ToBsonDocument();
        ApplyDetails(site, UpdateOverseasSiteRules.GrantedDetails(OverseasSiteDetails.From(site), item));
        var interims = ApplyInterimChanges(site, item, decidedAt);
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

    private static DateTime ToMilliseconds(DateTime instant)
    {
        var utc =
            instant.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(instant, DateTimeKind.Utc)
                : instant.ToUniversalTime();
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private const string NotAccreditedMessage =
        "The overseas site is no longer accredited on the accreditation, so its update cannot be granted.";

    private static OverseasSiteModel? AccreditedSite(
        AccreditationApplicationModel application,
        ChangeRequestSite item
    ) =>
        application.OverseasSites?.Sites.Find(s => s.SiteId == item.SiteId && s.OrsId == item.OrsId && s.Selected);

    // Why the update can't be granted on this read of the application, or null. Checked again on every re-read after
    // a lost race to mint an interim site number, so nothing is written onto an ORS the update no longer fits.
    private static IResult? RefuseGrant(AccreditationApplicationModel application, ChangeRequestSite item)
    {
        if (AccreditedSite(application, item) is not { } site)
            return Results.Conflict(NotAccreditedMessage);
        InterimSiteSync.Normalise(site);
        return RefuseInterimChanges(site, item);
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
                    change.Action == InterimSiteChangeAction.Add
                        ? "An interim site the update adds has no details, so the update cannot be granted."
                        : $"Interim site '{change.InterimSiteId}' is no longer on the overseas site as the update expects, so the update cannot be granted."
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
            site.SiteAddress = OverseasSiteAddress.Format(site.AddressLine1, site.TownOrCity, site.Country);
    }

    private sealed record LegacyTarget(
        AccreditationChangeRequest ChangeRequest,
        IAccreditationApplicationPersistence Applications,
        IServiceProvider Services,
        IClientSessionHandle? Session
    );

    // Each interim site the update adds that isn't on the ORS yet, given the next number through SiteIdAllocation as
    // the operator's own add is (AccreditationApplicationEndpoints.AddInterimSite): its 999 capacity check, and the
    // guarded write that persists the site only if no other writer has claimed the number, re-reading on a lost race.
    // Each change records the id of the interim site it added.
    private static async Task<(IResult? Error, AccreditationApplicationModel Application)> MintInterimSitesAsync(
        LegacyTarget target,
        AccreditationApplicationModel application,
        ChangeRequestSite item,
        DateTime decidedAt
    )
    {
        var claimed = new List<GrantedInterim>();
        foreach (var change in item.InterimSiteChanges.Where(c => c.Action == InterimSiteChangeAction.Add))
        {
            var added = AddedBefore(AccreditedSite(application, item)!, change, decidedAt, claimed);
            if (added is null)
            {
                var (error, created, persisted) = await SiteIdAllocation.AllocateGeneratedIdAsync(
                    target.Applications,
                    target.ChangeRequest.OrganisationId,
                    target.ChangeRequest.ApplicationId!,
                    application,
                    InterimSiteNumberAllocation(target, item, change.Proposed!, decidedAt),
                    reread => RefuseGrant(reread, item),
                    target.Session
                );
                if (error is not null || created is null || persisted is null)
                    return (error ?? Results.Problem("Failed to add an interim site."), application);
                (added, application) = (created, persisted);
            }
            change.InterimSiteId = added.SiteId;
            claimed.Add(new GrantedInterim(added, InterimSiteStatus.Active));
        }
        return (null, application);
    }

    private static GeneratedIdAllocation<InterimSiteModel> InterimSiteNumberAllocation(
        LegacyTarget target,
        ChangeRequestSite item,
        InterimSiteDetails details,
        DateTime decidedAt
    ) =>
        new(
            CapacityNoun: "interim sites",
            IdName: "interim site number",
            Scope: application =>
                SiteIdAllocation.InterimSiteNumberScopeAsync(
                    target.Applications,
                    application,
                    target.Services.GetRequiredService<IModelInterimSiteNumbers>(),
                    target.Session
                ),
            Attach: (application, siteNumber) =>
            {
                // RefuseGrant has found the ORS on this read and normalised its interim sites.
                var site = AccreditedSite(application, item)!;
                var interim = SiteIdAllocation.BuildInterimSite(
                    SiteIdAllocation.NextSiteId(application.OverseasSites!),
                    siteNumber,
                    details,
                    decidedAt
                );
                site.InterimSites.Add(interim);
                InterimSiteSync.SyncMirror(site);
                return (null, interim);
            },
            WriteIfAbsent: (application, siteNumber) =>
                target.Applications.UpdateIfInterimSiteNumberAbsentAsync(application, siteNumber, target.Session)
        );

    // The update's interim site changes applied to the ORS. The sites it adds are already on it (MintInterimSitesAsync).
    private static List<GrantedInterim> ApplyInterimChanges(
        OverseasSiteModel site,
        ChangeRequestSite item,
        DateTime decidedAt
    )
    {
        var granted = new List<GrantedInterim>();
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
                    var added =
                        AddedBefore(site, change, decidedAt, granted)
                        ?? throw new InvalidOperationException(
                            $"Interim site '{change.InterimSiteId}' the update added is not on the overseas site."
                        );
                    granted.Add(new GrantedInterim(added, InterimSiteStatus.Active));
                    break;
            }
        }
        return granted;
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
        var accreditation =
            await ChangeRequestGrant.FindAccreditationAsync(stores, entity, session, cancellationToken)
            ?? throw new InvalidOperationException($"Application {applicationId} has no Accreditation.");
        var entry =
            accreditation.AccreditationOverseasSites.Find(e => e.OverseasSiteId == ors.EntityId)
            ?? throw new InvalidOperationException(
                $"Application {applicationId}'s Accreditation has no entry for ORS {ors.OrsId}."
            );
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
            ors.FormattedAddress = OverseasSiteAddress.Format(ors.Address.Line1, ors.Address.TownOrCity, ors.Country);
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
