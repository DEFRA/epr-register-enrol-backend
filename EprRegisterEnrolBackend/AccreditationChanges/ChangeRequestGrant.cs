using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-495: grants an add-ORS change request item, accredited from the request's submission date: the legacy
/// accreditation application's existing (unselected) site is selected, and in the domain model the operator's
/// Accreditation gains an entry for the ORS in a new version and the ORS gets its first approved date if it has none.
/// It runs inside the dual-write unit of work with the item's own write, so they all commit or roll back together.
/// Not the approve path of a whole application (ApplicationModelSync's), which runs only when an application itself
/// becomes Approved.
/// </summary>
/// <remarks>
/// Each write is skipped when it is already made, so a grant replayed after a partial write without a transaction
/// (dual-write off, in Development only) completes it rather than doing it twice.
/// </remarks>
internal static class ChangeRequestGrant
{
    /// <summary>
    /// Applies the grant to everything but the item, which the caller writes last. Returns a 409 before any write
    /// when there is nothing to grant: a retry would fail the same way.
    /// </summary>
    /// <exception cref="ChangeRequestWriteConflictException">The legacy application changed since it was read.</exception>
    /// <exception cref="InvalidOperationException">The domain model lacks the application, Accreditation or ORS.</exception>
    public static async Task<IResult?> ApplyAsync(
        AccreditationChangeRequest changeRequest,
        ChangeRequestSite item,
        IAccreditationApplicationPersistence applications,
        IServiceProvider services,
        IClientSessionHandle? session,
        CancellationToken cancellationToken
    )
    {
        if (changeRequest.SubmittedAt is not { } submittedAt || changeRequest.ApplicationId is null)
            return Results.Conflict("The change request has no submission date or accreditation to grant it on.");

        var application = await applications.GetByIdAsync(
            changeRequest.OrganisationId,
            changeRequest.ApplicationId,
            session
        );
        var site = application?.OverseasSites?.Sites.Find(s =>
            s.SiteId == item.SiteId && s.OrsId == item.OrsId
        );
        if (application is null || site is null)
            return Results.Conflict("The overseas site is no longer on the accreditation, so it cannot be granted.");

        if (!site.Selected)
        {
            site.Selected = true;
            site.ValidFrom = SiteApprovalDate.Format(submittedAt);
            if (await applications.UpdateAsync(application, session) is null)
                throw new ChangeRequestWriteConflictException();
        }

        // Without dual-write there is no session and no model to write, as for every other write path.
        if (session is not null)
        {
            await GrantInModelAsync(
                services.GetRequiredService<ApplicationGroupStores>(),
                application,
                site,
                submittedAt,
                session,
                cancellationToken
            );
        }

        return null;
    }

    private static async Task GrantInModelAsync(
        ApplicationGroupStores stores,
        AccreditationApplicationModel application,
        OverseasSiteModel site,
        DateTime submittedAt,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var applicationId = application.Id!.Value;
        // The unit of work migrated the application's group before this ran, so each of these exists unless the
        // migration recorded the group as a conflict; then nothing is granted, and the push fails until it is fixed.
        var entity =
            await stores.Applications.Versions.FindCurrentAsync(
                Builders<ApplicationEntity>.Filter.Eq(a => a.EntityId, applicationId),
                session,
                cancellationToken
            ) ?? throw new InvalidOperationException($"Application {applicationId} is not in the domain model.");
        var registrationId =
            entity.RegistrationId
            ?? throw new InvalidOperationException($"Application {applicationId} has no Registration.");
        var ors =
            await stores.OverseasSites.Versions.FindCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(s => s.RegistrationId, registrationId)
                    & Builders<OverseasSiteEntity>.Filter.Eq(s => s.OrsId, site.OrsId),
                session,
                cancellationToken
            ) ?? throw new InvalidOperationException($"Registration {registrationId} has no ORS {site.OrsId}.");
        var accreditation =
            await FindAccreditationAsync(stores, entity, session, cancellationToken)
            ?? throw new InvalidOperationException($"Application {applicationId} has no Accreditation.");

        var change = ChangeInfo.Of(ChangeReason.ChangeRequestGranted, applicationId, ors.OrsId);
        if (ors.FirstApprovedAt is null)
        {
            ors.RecordFirstApproval(submittedAt, FirstApprovalSource.ThisService);
            await stores.OverseasSites.Versions.UpdateAsync(ors, ors.Version, change, session, cancellationToken);
        }

        if (!accreditation.AccreditationOverseasSites.Exists(e => e.OverseasSiteId == ors.EntityId))
        {
            accreditation.AccreditationOverseasSites.Add(AccreditationFromApplication.SiteEntry(site, ors.EntityId));
            await stores.Accreditations.Versions.UpdateAsync(
                accreditation,
                accreditation.Version,
                change,
                session,
                cancellationToken
            );
        }
    }

    // The Accreditation the application points at, else the one it approved (unique on applicationId).
    private static async Task<AccreditationEntity?> FindAccreditationAsync(
        ApplicationGroupStores stores,
        ApplicationEntity entity,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    ) =>
        (
            entity.AsAccreditation().AccreditationId is { } accreditationId
                ? await stores.Accreditations.Versions.GetCurrentAsync(accreditationId, session, cancellationToken)
                : null
        )
        ?? await stores.Accreditations.Versions.FindCurrentAsync(
            Builders<AccreditationEntity>.Filter.Eq(a => a.ApplicationId, entity.EntityId),
            session,
            cancellationToken
        );
}

/// <summary>
/// RA-495: a change request or its accreditation application changed between read and write. In a transaction it
/// rolls the whole push back; without one, the push is read again.
/// </summary>
internal sealed class ChangeRequestWriteConflictException()
    : Exception("The change request or its accreditation was changed at the same time.");
