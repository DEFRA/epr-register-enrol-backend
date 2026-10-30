using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// The Accreditation an approved application creates. The migration (§6.1 step 5) and a live approval (B9) both
/// build it here, so reconciliation (B10) can never see them disagree.
/// </summary>
public static class AccreditationFromApplication
{
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
