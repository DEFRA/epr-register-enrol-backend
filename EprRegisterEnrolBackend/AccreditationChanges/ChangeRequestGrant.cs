using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-495: grants an add-ORS change request item, accredited from the request's submission date: the legacy
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
            ApplySuppliedAnswers(site, item.Answers);
            AddBesEvidence(site, item.BesEvidence);
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
                item.Answers,
                submittedAt,
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
    /// </summary>
    private static void AddBesEvidence(OverseasSiteModel site, BesEvidenceModel? evidence)
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
        // One ORS version for the grant; a versioned update that changes nothing writes nothing, so a replay is a
        // no-op.
        ApplySuppliedAnswers(ors, answers, site);
        if (ors.FirstApprovedAt is null)
            ors.RecordFirstApproval(submittedAt, FirstApprovalSource.ThisService);
        await stores.OverseasSites.Versions.UpdateAsync(ors, ors.Version, change, session, cancellationToken);

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
