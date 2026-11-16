using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-492 rules for an add-overseas-site change request: which ORS fields are locked, what is
/// still missing (AC02), whether BES evidence is needed, and the charge (AC03/AC04).
/// </summary>
public static class ChangeRequestRules
{
    public const string Country = "country";
    public const string Coordinates = "coordinates";
    public const string OperationCodes = "operationCodes";
    public const string ConditionsOfExport = "conditionsOfExport";
    public const string BesEvidence = "besEvidence";

    public static readonly IReadOnlyDictionary<string, string> MissingFieldMessages =
        new Dictionary<string, string>
        {
            [Country] = "Enter the country of the overseas reprocessing site.",
            [Coordinates] = "Enter the coordinates of the overseas reprocessing site.",
            [OperationCodes] = "Select the recycling operation (R code) for the overseas reprocessing site.",
            [ConditionsOfExport] = "Confirm whether the conditions of export are met.",
            [BesEvidence] = "Upload evidence of broadly equivalent standards for the overseas reprocessing site.",
        };

    /// <summary>
    /// The site's status on the accreditation, partitioned as RA-425's Manage overseas sites page does: selected is
    /// Accredited; otherwise an existing site is Registered. Null for a site added on a reaccreditation application
    /// and never registered (<see cref="OverseasSiteModel.IsNewSite"/>), which is neither and cannot be added.
    /// RA-739: also null for an unselected site without an ORS id (pre-RA-507 data), since Case Management
    /// needs every added site's ORS id, as the registration-only view leaves out an ORS without one.
    /// </summary>
    public static SiteAccreditationStatus? AccreditationStatus(OverseasSiteModel site) =>
        site switch
        {
            { Selected: true } => SiteAccreditationStatus.Accredited,
            { IsNewSite: true } => null,
            _ when string.IsNullOrWhiteSpace(site.OrsId) => null,
            _ => SiteAccreditationStatus.Registered,
        };

    /// <summary>
    /// The four fields the ticket locks once the ORS holds a value for them; a field the ORS
    /// lacks is required and may be supplied on the request instead.
    /// </summary>
    public static List<string> LockedFields(OverseasSiteModel? site)
    {
        var locked = new List<string>();
        if (site is null)
            return locked;
        if (!string.IsNullOrWhiteSpace(site.Country))
            locked.Add(Country);
        if (!string.IsNullOrWhiteSpace(site.Coordinates))
            locked.Add(Coordinates);
        if (site.OperationCodes is { Count: > 0 })
            locked.Add(OperationCodes);
        if (site.ConditionsOfExport is not null)
            locked.Add(ConditionsOfExport);
        return locked;
    }

    public static ChangeRequestSiteAnswers Effective(
        OverseasSiteModel? site,
        ChangeRequestSiteAnswers answers
    ) =>
        new()
        {
            Country = !string.IsNullOrWhiteSpace(site?.Country) ? site.Country : answers.Country,
            Coordinates = !string.IsNullOrWhiteSpace(site?.Coordinates)
                ? site.Coordinates
                : answers.Coordinates,
            OperationCodes = site?.OperationCodes is { Count: > 0 }
                ? site.OperationCodes
                : answers.OperationCodes,
            ConditionsOfExport = site?.ConditionsOfExport ?? answers.ConditionsOfExport,
        };

    public static List<string> SuppliedFields(ChangeRequestSiteAnswers answers)
    {
        var supplied = new List<string>();
        if (answers.Country is not null)
            supplied.Add(Country);
        if (answers.Coordinates is not null)
            supplied.Add(Coordinates);
        if (answers.OperationCodes is not null)
            supplied.Add(OperationCodes);
        if (answers.ConditionsOfExport is not null)
            supplied.Add(ConditionsOfExport);
        return supplied;
    }

    /// <summary>
    /// Same rule as the application's BES section (SectionStatusService.IsBesEvidenceComplete):
    /// evidence is needed for a site outside the EU and OECD unless conditions of export are met.
    /// </summary>
    public static bool IsBesEvidenceRequired(
        OverseasSiteModel? site,
        ChangeRequestSiteAnswers effective
    ) => site is { IsEu: false, IsOecd: false } && effective.ConditionsOfExport != true;

    public static bool HasCleanBesEvidence(BesEvidenceModel? evidence)
    {
        var uploads = evidence?.BesEvidenceUploads ?? [];
        return uploads.Count > 0 && uploads.All(u => u.ScanStatus == "Clean");
    }

    public static List<string> MissingFields(OverseasSiteModel? site, ChangeRequestSite requestSite)
    {
        var effective = Effective(site, requestSite.Answers);
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(effective.Country))
            missing.Add(Country);
        if (string.IsNullOrWhiteSpace(effective.Coordinates))
            missing.Add(Coordinates);
        if (effective.OperationCodes is not { Count: > 0 })
            missing.Add(OperationCodes);
        if (effective.ConditionsOfExport is null)
            missing.Add(ConditionsOfExport);
        if (IsBesEvidenceRequired(site, effective) && !HasCleanBesEvidence(requestSite.BesEvidence))
            missing.Add(BesEvidence);
        return missing;
    }

    public static ChargeView Charge(int chargeableSites) =>
        new(
            chargeableSites,
            AccreditationChargeCalculator.OverseasSiteFeePencePerSite,
            AccreditationChargeCalculator.CalculateInYearOverseasSitesChargePence(chargeableSites)
        );

    public static OverseasSiteModel? FindSite(AccreditationApplicationModel accreditation, int siteId) =>
        accreditation.OverseasSites?.Sites.FirstOrDefault(s => s.SiteId == siteId);

    /// <summary>
    /// RA-697: a registration-only item's SiteId is its ORS id's integer value, which need not be the
    /// accreditation's key for the same ORS, so the ORS id is the match whenever the item and the site both have one.
    /// </summary>
    public static bool IsForSite(ChangeRequestSite item, OverseasSiteModel site) =>
        item.OrsId is not null && site.OrsId is not null ? item.OrsId == site.OrsId : item.SiteId == site.SiteId;

    public static ChangeRequestView ToView(
        AccreditationChangeRequest request,
        AccreditationApplicationModel accreditation
    ) => ToView(request, accreditation.OverseasSites?.Sites ?? []);

    /// <summary>RA-697: against a registration's sites, for a registration-only request.</summary>
    public static ChangeRequestView ToView(
        AccreditationChangeRequest request,
        IReadOnlyList<OverseasSiteModel> orsSites
    )
    {
        var sites = request
            .Sites.Select(requestSite =>
            {
                var site = orsSites.FirstOrDefault(s => IsForSite(requestSite, s));
                var effective = Effective(site, requestSite.Answers);
                var missing = MissingFields(site, requestSite);
                return new ChangeRequestSiteView(
                    requestSite.SiteId,
                    requestSite.OrsId,
                    requestSite.SiteName,
                    requestSite.Answers,
                    effective,
                    IsBesEvidenceRequired(site, effective),
                    requestSite.BesEvidence,
                    missing,
                    missing.Count == 0,
                    requestSite.ItemId,
                    requestSite.ChangeType,
                    requestSite.Status,
                    requestSite.ValidFrom,
                    requestSite.WithdrawnAt,
                    requestSite.CaseManagementReference
                );
            })
            .ToList();

        return new ChangeRequestView(
            request.RequestId,
            request.ChangeType,
            request.Status,
            request.CreatedAt,
            request.UpdatedAt,
            request.SubmittedAt,
            request.SubmittedBy,
            sites,
            sites.Count > 0 && sites.All(s => s.IsComplete),
            Charge(request.Sites.Count),
            request.TonnageBandUpgrade is { } tonnage ? TonnageBandUpgradeView.From(tonnage) : null
        );
    }
}

public sealed record ChargeView(int ChargeableSites, int ChargePerSitePence, int TotalPence);

public sealed record ChangeRequestSiteView(
    int SiteId,
    string? OrsId,
    string SiteName,
    ChangeRequestSiteAnswers Answers,
    ChangeRequestSiteAnswers Effective,
    bool BesEvidenceRequired,
    BesEvidenceModel? BesEvidence,
    List<string> MissingFields,
    bool IsComplete,
    Guid ItemId,
    string ChangeType,
    ChangeItemStatus Status,
    DateTime? ValidFrom,
    DateTime? WithdrawnAt,
    string? CaseManagementReference
);

/// <summary>
/// RA-739: no request-level reference: each item carries its own Case Management reference, and the submission
/// reference they share is not shown to the operator.
/// </summary>
public sealed record ChangeRequestView(
    string RequestId,
    string ChangeType,
    ChangeRequestStatus Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SubmittedAt,
    SubmittedByModel? SubmittedBy,
    List<ChangeRequestSiteView> Sites,
    bool IsComplete,
    ChargeView Charge,
    TonnageBandUpgradeView? TonnageBandUpgrade
);

/// <summary>RA-697: the API's shape for a tonnage band upgrade item, kept apart from the stored model.</summary>
public sealed record TonnageBandUpgradeView(
    Guid ItemId,
    string ChangeType,
    ChangeItemStatus Status,
    DateTime? ValidFrom,
    DateTime? WithdrawnAt,
    PlannedTonnageBand RequestedBand,
    string? CaseManagementReference
)
{
    public static TonnageBandUpgradeView From(TonnageBandUpgradeItem item) =>
        new(
            item.ItemId,
            item.ChangeType,
            item.Status,
            item.ValidFrom,
            item.WithdrawnAt,
            item.RequestedBand,
            item.CaseManagementReference
        );
}

public sealed record AccreditationChangeSiteView(
    int SiteId,
    string? OrsId,
    SiteAccreditationStatus AccreditationStatus,
    string? PendingRequestId,
    ChangeRequestStatus? PendingRequestStatus,
    List<string> LockedFields,
    OverseasSiteModel Site
);

/// <summary>RA-697: <see cref="ApplicationId"/> and <see cref="AccreditationReference"/> are null for a registration-only view.</summary>
public sealed record AccreditationChangesView(
    string OrganisationId,
    string? ApplicationId,
    string? OrganisationName,
    string? RegistrationId,
    MaterialType MaterialType,
    int Year,
    string? AccreditationReference,
    string? RegistrationReference,
    List<AccreditationChangeSiteView> Sites,
    ChangeRequestView? ActiveRequest,
    List<ChangeRequestView> Requests,
    List<string> PendingOrsIds
);
