using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// How a new overseas or interim site gets its ids: the internal <see cref="OverseasSiteModel.SiteId"/> that routes
/// address, the registration-wide ORS id (RA-482) and the registration-wide interim site number (RA-603). Shared by the
/// operator's own add routes and (RA-782) the grant of an approved ORS update that adds interim sites.
/// </summary>
public static class SiteIdAllocation
{
    /// <summary>
    /// Site numbers must be unique application-wide across both ORS sites and their nested interim sites (RA-294), so
    /// the next id is the max across both, not just the ORS list.
    /// </summary>
    public static int NextSiteId(AccreditationApplicationOverseasSites overseasSites)
    {
        var maxSiteId = 0;
        foreach (var site in overseasSites.Sites)
        {
            if (site.SiteId > maxSiteId)
                maxSiteId = site.SiteId;

            // RA-603: every interim site, the legacy mirror and withdrawn ones included - their
            // ids stay claimed for as long as the record does, which is forever (AC05). Reads
            // only; a caller that is about to mutate the list normalises it itself.
            foreach (var interimSiteId in InterimSiteSync.All(site).Select(i => i.SiteId))
            {
                if (interimSiteId > maxSiteId)
                    maxSiteId = interimSiteId;
            }
        }
        return maxSiteId + 1;
    }

    /// <summary>
    /// RA-482: the OrsId allocation scope. A registration is established once and renewed annually via a new
    /// AccreditationApplicationModel each year, so RegistrationId (not the non-nullable OrganisationId) is what
    /// actually identifies "this registration" across every year's application. RA-516: GetOrsIdsByRegistrationAsync
    /// filters by RegistrationId server-side. Every OrsId string is included regardless of Selected status or origin
    /// (operator-added or ReEx-seeded): a deselected site keeps its id permanently. When RegistrationId is null there
    /// is by definition no cross-year history to scope against, so this falls back to the application's own sites.
    /// </summary>
    public static async Task<IEnumerable<string?>> OrsIdScopeAsync(
        IAccreditationApplicationPersistence persistence,
        AccreditationApplicationModel application,
        IClientSessionHandle? session
    )
    {
        if (application.RegistrationId is null)
            return (application.OverseasSites?.Sites ?? []).Select(s => s.OrsId);

        return await persistence.GetOrsIdsByRegistrationAsync(application.RegistrationId, session);
    }

    /// <summary>
    /// RA-603: the interim site NUMBER allocation scope, not to be confused with <see cref="NextSiteId"/>, which
    /// allocates the internal SiteId. The two used to be the same counter, which is why interim numbers skipped
    /// whenever an ORS was added. Scoped exactly like <see cref="OrsIdScopeAsync"/>, for the same reason: a number
    /// reissued next year would collide with one already sitting in a submitted return. Withdrawn interim sites are
    /// included: their numbers stay claimed for as long as the record does, which under AC05 is permanently. Legacy
    /// SN-000N values come through untouched and simply do not parse, which is what makes them harmless here.
    /// RA-644 B8d: with DualWrite on, the numbers the domain model holds count too. The migration can renumber a
    /// legacy SN-000N into the 3-digit pool, and legacy alone would hand that number out again.
    /// </summary>
    public static async Task<IEnumerable<string?>> InterimSiteNumberScopeAsync(
        IAccreditationApplicationPersistence persistence,
        AccreditationApplicationModel application,
        IModelInterimSiteNumbers modelNumbers,
        IClientSessionHandle? session
    )
    {
        var model = await modelNumbers.GetAsync(application.RegistrationEntityId, session);
        if (application.RegistrationId is null)
            return (application.OverseasSites?.Sites ?? [])
                .SelectMany(InterimSiteSync.All)
                .Select(i => i.SiteNumber)
                .Concat(model);

        return (await persistence.GetInterimSiteNumbersByRegistrationAsync(application.RegistrationId, session)).Concat(
            model
        );
    }

    /// <summary>
    /// A new interim site, approved from <paramref name="createdAt"/> (RA-781's history starts there). New to the
    /// regulator, so its "new" badge is on (RA-292 AC02).
    /// </summary>
    public static InterimSiteModel BuildInterimSite(
        int siteId,
        string siteNumber,
        IInterimSiteFields details,
        DateTime createdAt
    ) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            Country = details.Country,
            SiteName = details.SiteName,
            AddressLine1 = details.AddressLine1,
            AddressLine2 = details.AddressLine2,
            TownOrCity = details.TownOrCity,
            StateOrRegion = details.StateOrRegion,
            Postcode = details.Postcode,
            ContactName = details.ContactName,
            ContactEmail = details.ContactEmail,
            ContactPhone = details.ContactPhone,
            OperationCodes = [.. details.OperationCodes],
            IsNewSite = true,
            CreatedAt = createdAt,
            StatusHistory = InterimSiteStatusHistory.Added(createdAt),
        };

    /// <summary>
    /// RA-482/RA-603: generate an id from the registration-wide scope, attach the new record, and persist only if no
    /// concurrent writer has claimed that id first. A lost race re-reads and tries again with a fresh id, bounded so a
    /// stuck conflict fails loudly. Reports either an error or the created record with its persisted application,
    /// never a mix.
    /// </summary>
    /// <remarks>
    /// The guarded write also compares Version, so it can lose to ANY concurrent write, a status change included. That
    /// is why the re-read is checked with <paramref name="rejectReread"/> before retrying: without it, a retry could add
    /// to an application that has since become terminal.
    /// </remarks>
    public static async Task<(IResult? ErrorResult, T? Created, AccreditationApplicationModel? Updated)> AllocateGeneratedIdAsync<T>(
        IAccreditationApplicationPersistence persistence,
        string organisationId,
        string applicationId,
        AccreditationApplicationModel application,
        GeneratedIdAllocation<T> allocation,
        Func<AccreditationApplicationModel, IResult?> rejectReread,
        IClientSessionHandle? session
    )
        where T : class
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var generated = OrsIdGenerator.GenerateNext(await allocation.Scope(application));
            if (generated.CapacityExceeded)
                return (
                    Results.UnprocessableEntity(
                        $"This registration has reached the maximum of 999 {allocation.CapacityNoun}."
                    ),
                    null,
                    null
                );

            var (attachError, created) = allocation.Attach(application, generated.OrsId!);
            if (attachError is not null || created is null)
                return (attachError ?? Results.Problem("Failed to attach the new site."), null, null);

            var updated = await allocation.WriteIfAbsent(application, generated.OrsId!);
            if (updated is not null)
                return (null, created, updated);

            var refetched = await persistence.GetByIdAsync(organisationId, applicationId, session);
            if (refetched is null)
                return (Results.NotFound(), null, null);
            if (rejectReread(refetched) is { } rejected)
                return (rejected, null, null);
            application = refetched;
        }

        return (
            Results.Conflict($"Could not allocate a unique {allocation.IdName} after several attempts; please retry."),
            null,
            null
        );
    }
}
