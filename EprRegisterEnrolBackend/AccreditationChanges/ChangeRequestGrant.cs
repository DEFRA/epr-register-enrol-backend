using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-495: grants an add-ORS change request item, accredited from the effective date Case Management sent (AC6: the
/// date the caseworker entered on approval): the legacy
/// accreditation application's existing (unselected) site is selected, and in the domain model the operator's
/// Accreditation gains an entry for the ORS in a new version and the ORS gets its first approved date if it has none.
/// 3bb.29: the answers and BES evidence the operator supplied on the item are written as the operator's own
/// add-to-accreditation journey writes them: onto the legacy site, and so (through the unit of work's sync and the
/// Accreditation entry) into the model, with the answers on the ORS too.
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
    /// when there is nothing to grant (not an add-ORS or update-ORS item, no site, or a site already accredited another
    /// way): a retry would fail the same way. RA-782: an update-ORS item is granted by
    /// <see cref="ChangeRequestUpdateGrant"/>.
    /// </summary>
    /// <exception cref="ChangeRequestWriteConflictException">The legacy application changed since it was read.</exception>
    /// <exception cref="InvalidOperationException">The domain model lacks the application, Accreditation or ORS.</exception>
    public static async Task<IResult?> ApplyAsync(
        AccreditationChangeRequest changeRequest,
        ChangeRequestSite item,
        ChangeRequestDecision decision,
        IAccreditationApplicationPersistence applications,
        IServiceProvider services,
        IClientSessionHandle? session,
        CancellationToken cancellationToken
    )
    {
        if (item.IsUpdate)
            return await ChangeRequestUpdateGrant.ApplyAsync(
                changeRequest,
                item,
                decision.DecidedAt,
                applications,
                services,
                session,
                cancellationToken
            );

        // An add-ORS item is granted by accrediting its site; any other change type has no grant here.
        if (item.ChangeType != AccreditationChangeRequest.AddOverseasSiteChangeType)
            return Results.Conflict($"A '{item.ChangeType}' change request item cannot be granted.");

        if (changeRequest.SubmittedAt is null || changeRequest.ApplicationId is null)
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

        var accreditedFrom = decision.ValidFrom;
        // A selected site is this grant's own earlier write only when it carries this grant's date; selected any other
        // way, it is already accredited and granting it would leave its date, answers and BES evidence half-applied.
        var validFrom = SiteApprovalDate.Format(accreditedFrom);
        if (site.Selected && site.ValidFrom != validFrom)
            return Results.Conflict("The overseas site is already accredited on the accreditation, so it cannot be granted.");

        if (!site.Selected)
        {
            site.Selected = true;
            site.ValidFrom = validFrom;
            ApplySuppliedAnswers(site, item.Answers);
            AddBesEvidence(site, item.BesEvidence);
            if (await applications.UpdateAsync(application, session) is null)
                throw new ChangeRequestWriteConflictException();
        }

        // Without dual-write there is no session and no model to write, as for every other write path. The model's
        // stores are resolved only here, not injected: they exist only with dual-write on, and building them reaches
        // Mongo, which a push without a session must not wait on.
        if (session is not null)
        {
            await GrantInModelAsync(
                services.GetRequiredService<ApplicationGroupStores>(),
                application,
                site,
                item.Answers,
                accreditedFrom,
                session,
                cancellationToken
            );
        }

        return null;
    }

    /// <summary>
    /// 3bb.29: the answers the operator supplied on the request, written onto the legacy site as the operator's own
    /// add-to-accreditation journey (<c>PromoteOverseasSite</c>) writes them: the country with its EU and OECD
    /// classification, the coordinates, the R codes and the conditions of export answer. Only what was supplied; the
    /// site's other details are its registered ones and are left alone.
    /// </summary>
    private static void ApplySuppliedAnswers(OverseasSiteModel site, ChangeRequestSiteAnswers answers)
    {
        if (answers.Country is { } country)
        {
            site.Country = country;
            site.IsEu = CountryClassifications.IsEu(country);
            site.IsOecd = CountryClassifications.IsOecd(country);
        }

        if (answers.Coordinates is { } coordinates)
            site.Coordinates = coordinates;
        if (answers.OperationCodes is { } operationCodes)
            site.OperationCodes = [.. operationCodes];
        if (answers.ConditionsOfExport is { } conditionsOfExport)
            site.ConditionsOfExport = conditionsOfExport;
    }

    /// <summary>
    /// 3bb.29: the request's BES evidence files join the site's, as the operator's own BES upload adds each file to
    /// the site (<c>AddBesEvidenceFile</c>). A file the site already holds is not added again.
    /// 3bb.38: a grant cannot refuse, so the request's own upload held the files to the application's duplicate
    /// filename rule and per-site cap across these and the site's (<c>ChangeRequestRules.GrantedBesEvidence</c>).
    /// </summary>
    internal static void AddBesEvidence(OverseasSiteModel site, BesEvidenceModel? evidence)
    {
        if (evidence is not { BesEvidenceUploads.Count: > 0 })
            return;

        site.BesEvidence ??= new BesEvidenceModel
        {
            DoYouWantToUploadMoreEvidence = evidence.DoYouWantToUploadMoreEvidence,
        };
        var held = site.BesEvidence.BesEvidenceUploads.Select(f => f.FileId).ToHashSet();
        site.BesEvidence.BesEvidenceUploads.AddRange(
            evidence.BesEvidenceUploads.Where(f => !held.Contains(f.FileId))
        );
    }

    /// <summary>
    /// 3bb.29: the same supplied answers onto the ORS, taken from the legacy site that now holds them, as a whole
    /// application's approval carries its sites' details to their ORSs (<c>LegacySiteMapper.WithDetailsOf</c>): the
    /// country is also the address's. Only the supplied fields, so the ORS's other details are not overwritten with
    /// the accreditation application's copy of them.
    /// </summary>
    private static void ApplySuppliedAnswers(
        OverseasSiteEntity ors,
        ChangeRequestSiteAnswers answers,
        OverseasSiteModel site
    )
    {
        if (answers.Country is not null)
        {
            ors.Country = site.Country;
            ors.Address ??= new PostalAddress();
            ors.Address.Country = site.Country;
            ors.IsEu = site.IsEu;
            ors.IsOecd = site.IsOecd;
        }

        if (answers.Coordinates is not null)
            ors.Coordinates = site.Coordinates;
        if (answers.OperationCodes is not null)
            ors.OperationCodes = [.. site.OperationCodes];
        if (answers.ConditionsOfExport is not null)
            ors.ConditionsOfExport = site.ConditionsOfExport;
    }

    private static async Task GrantInModelAsync(
        ApplicationGroupStores stores,
        AccreditationApplicationModel application,
        OverseasSiteModel site,
        ChangeRequestSiteAnswers answers,
        DateTime accreditedFrom,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var applicationId = application.Id!.Value;
        var (entity, ors) = await FindInModelAsync(stores, applicationId, site.OrsId, session, cancellationToken);
        var accreditation =
            await FindAccreditationAsync(stores, entity, session, cancellationToken)
            ?? throw new InvalidOperationException($"Application {applicationId} has no Accreditation.");

        var change = ChangeInfo.Of(ChangeReason.ChangeRequestGranted, applicationId, ors.OrsId);
        // One ORS version for the grant; a versioned update that changes nothing writes nothing, so a replay is a
        // no-op.
        // The ORS's first approved date is when it was first approved ever, so one it already has stays. It is not what
        // the site is served as validFrom: that is the Accreditation entry's, below (3bb.43).
        ApplySuppliedAnswers(ors, answers, site);
        if (ors.FirstApprovedAt is null)
            ors.RecordFirstApproval(accreditedFrom, FirstApprovalSource.ChangeRequest);
        await stores.OverseasSites.Versions.UpdateAsync(ors, ors.Version, change, session, cancellationToken);

        // The entry records the date legacy writes as this accreditation's site validFrom, so it is served to this
        // accreditation's application only, not to every application holding the ORS (3bb.44). It is added once, with
        // the legacy write it mirrors, so a replay writes neither.
        if (!accreditation.AccreditationOverseasSites.Exists(e => e.OverseasSiteId == ors.EntityId))
        {
            var entry = AccreditationFromApplication.SiteEntry(site, ors.EntityId);
            entry.AccreditedFrom = accreditedFrom;
            accreditation.AccreditationOverseasSites.Add(entry);
            await stores.Accreditations.Versions.UpdateAsync(
                accreditation,
                accreditation.Version,
                change,
                session,
                cancellationToken
            );
        }
    }

    /// <summary>
    /// The application and the ORS in the domain model. The unit of work migrated the application's group before the
    /// grant ran, so each exists unless the migration recorded the group as a conflict; then nothing is granted, and
    /// the push fails until it is fixed.
    /// </summary>
    internal static async Task<(ApplicationEntity Application, OverseasSiteEntity Ors)> FindInModelAsync(
        ApplicationGroupStores stores,
        ObjectId applicationId,
        string? orsId,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
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
                    & Builders<OverseasSiteEntity>.Filter.Eq(s => s.OrsId, orsId),
                session,
                cancellationToken
            ) ?? throw new InvalidOperationException($"Registration {registrationId} has no ORS {orsId}.");
        return (entity, ors);
    }

    // The Accreditation the application points at, else the one it approved (unique on applicationId).
    internal static async Task<AccreditationEntity?> FindAccreditationAsync(
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
/// RA-782: what Case Management decided for an item: the date the granted change takes effect, and the push's
/// <c>occurredAt</c>, which stamps what the grant writes.
/// </summary>
internal sealed record ChangeRequestDecision(DateTime ValidFrom, DateTime DecidedAt);

/// <summary>
/// RA-495: a change request or its accreditation application changed between read and write. In a transaction it
/// rolls the whole push back; without one, the push is read again.
/// </summary>
public sealed class ChangeRequestWriteConflictException()
    : Exception("The change request or its accreditation was changed at the same time.");
