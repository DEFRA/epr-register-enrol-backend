using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// The Accreditation an approved application creates. The migration (§6.1 step 5) and a live approval (B9) both
/// build it here, so reconciliation (B10) can never see them disagree.
/// </summary>
public static class AccreditationFromApplication
{
    /// <summary>What decides whether an approved application gets an Accreditation; see <see cref="FindAsync"/>.</summary>
    /// <param name="Own">The application's Accreditation, when it already has one (unique on <c>applicationId</c>).</param>
    /// <param name="SameYear">Another Accreditation for the registration and year, looked for only without <paramref name="Own"/>.</param>
    /// <param name="NumberMissing">The application has no accreditation number.</param>
    public sealed record Lookup(
        AccreditationEntity? Own,
        AccreditationEntity? SameYear,
        bool NumberMissing
    );

    /// <summary>
    /// The facts the migration and a live approval both act on. They react differently: the migration records a
    /// conflict, a live approval refuses (D9), but they find the same things in the same way.
    /// </summary>
    public static async Task<Lookup> FindAsync(
        IVersionedPersistence<AccreditationEntity> accreditations,
        ObjectId registrationId,
        AccreditationApplicationModel application,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var versions = accreditations.Versions;
        var filter = Builders<AccreditationEntity>.Filter;
        var own = await versions.FindCurrentAsync(
            filter.Eq(a => a.ApplicationId, application.Id),
            session,
            cancellationToken
        );
        var sameYear = own is not null
            ? null
            : await versions.FindCurrentAsync(
                filter.Eq(a => a.RegistrationId, registrationId)
                    & filter.Eq(a => a.Year, application.Year),
                session,
                cancellationToken
            );
        return new Lookup(
            own,
            sameYear,
            string.IsNullOrWhiteSpace(application.AccreditationReference)
        );
    }

    /// <summary>Inserts the Accreditation <see cref="Build"/> makes, as its first version.</summary>
    public static async Task<AccreditationEntity> InsertAsync(
        IVersionedPersistence<AccreditationEntity> accreditations,
        ObjectId registrationId,
        AccreditationApplicationModel application,
        Func<OverseasSiteModel, ObjectId?> overseasSiteIdOf,
        ChangeReason reason,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    ) =>
        await accreditations.Versions.InsertAsync(
            Build(registrationId, application, overseasSiteIdOf),
            ChangeInfo.Of(reason, application.Id),
            session,
            cancellationToken
        );

    /// <param name="overseasSiteIdOf">The ORS a legacy site maps to, or null to leave the site out.</param>
    /// <remarks>Not back-dated: the approval time is kept as an attribute, and the version starts when it is written.</remarks>
    public static AccreditationEntity Build(
        ObjectId registrationId,
        AccreditationApplicationModel application,
        Func<OverseasSiteModel, ObjectId?> overseasSiteIdOf
    ) =>
        new()
        {
            RegistrationId = registrationId,
            Year = application.Year,
            AccreditationNumber = string.IsNullOrWhiteSpace(application.AccreditationReference)
                ? null
                : application.AccreditationReference,
            ApplicationId = application.Id,
            Status = AccreditationStatus.Approved,
            ApprovedAt = application.CaseManagementStatusUpdatedAt,
            TonnageBand = TonnageBandConversion.FromLegacy(application.Prns.PlannedTonnageBand),
            Signatories = application
                .Prns.Authorisers.Select(a => new Signatory
                {
                    FullName = a.FullName,
                    Email = a.Email,
                })
                .ToList(),
            AccreditationOverseasSites = (application.OverseasSites?.Sites ?? [])
                .Where(s => s.Selected)
                .Select(s => (Site: s, OverseasSiteId: overseasSiteIdOf(s)))
                .Where(x => x.OverseasSiteId is not null)
                .Select(x => new AccreditationOverseasSiteEntry
                {
                    OverseasSiteId = x.OverseasSiteId!.Value,
                    OrsId = x.Site.OrsId,
                    Status = AccreditedSiteStatus.Approved,
                    BesEvidence =
                        LegacyApplicationMapper
                            .ToBesEvidence(x.Site.BesEvidence)
                            ?.BesEvidenceUploads
                        ?? [],
                })
                .ToList(),
        };
}
