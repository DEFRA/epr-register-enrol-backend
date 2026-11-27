using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-492 rules for an add-overseas-site change request: which ORS fields are locked, what is
/// still missing (AC02), whether BES evidence is needed, and the charge (AC03/AC04). RA-682: the rules every item
/// shares (charging, submission grouping, the view); an update item's own are in <see cref="UpdateOverseasSiteRules"/>.
/// </summary>
public static class ChangeRequestRules
{
    /// <summary>RA-682: the key an update with nothing changed is reported under.</summary>
    public const string Changes = "changes";

    /// <summary>RA-682: the key an update is reported under when its ORS is no longer accredited.</summary>
    public const string Site = "site";

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
            [Changes] = "Change at least one detail of the overseas reprocessing site, or remove it from the request.",
            [Site] =
                "This overseas reprocessing site is no longer approved, so its details can't be changed. "
                + "Remove it from the request.",
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
            // RA-698: the operator's changes win over the ORS's own values.
            SiteName = answers.SiteName ?? site?.SiteName,
            AddressLine1 = answers.AddressLine1 ?? site?.AddressLine1,
            AddressLine2 = answers.AddressLine2 ?? site?.AddressLine2,
            TownOrCity = answers.TownOrCity ?? site?.TownOrCity,
            StateOrRegion = answers.StateOrRegion,
            Postcode = answers.Postcode,
            ContactName = answers.ContactName ?? site?.ContactName,
            ContactEmail = answers.ContactEmail ?? site?.ContactEmail,
            ContactPhone = answers.ContactPhone ?? site?.ContactPhone,
            RejectedLoadsArrangements = answers.RejectedLoadsArrangements ?? site?.RepatriatedLoads,
            InterimSites = answers.InterimSites,
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
    /// 3bb.13: what the operator supplied on the item, as Case Management marks it: the answers above, and
    /// <see cref="BesEvidence"/> when the operator uploaded evidence on the request (sent as the site's BES evidence).
    /// </summary>
    public static List<string> SuppliedFields(ChangeRequestSite requestSite)
    {
        var supplied = SuppliedFields(requestSite.Answers);
        if (requestSite.BesEvidence is { BesEvidenceUploads.Count: > 0 })
            supplied.Add(BesEvidence);
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

    /// <summary>
    /// 3bb.38: the BES evidence the site would hold once the item is granted: the site's own files on the
    /// accreditation application, then the request's that it does not already hold (as <c>ChangeRequestGrant</c>
    /// adds them). The request's upload applies the application's per-site file cap to this.
    /// </summary>
    public static BesEvidenceModel GrantedBesEvidence(OverseasSiteModel? site, BesEvidenceModel? requested)
    {
        var held = site?.BesEvidence?.BesEvidenceUploads ?? [];
        var heldIds = held.Select(f => f.FileId).ToHashSet();
        return new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                .. held,
                .. (requested?.BesEvidenceUploads ?? []).Where(f => !heldIds.Contains(f.FileId)),
            ],
        };
    }

    /// <summary>
    /// RA-682 AC05: whether an item is charged. Only adding an ORS is; the charge, the payment reference and what Case
    /// Management is sent all follow this one rule.
    /// </summary>
    public static bool IsChargeable(ChangeRequestItem item) =>
        item.ChangeType == AccreditationChangeRequest.AddOverseasSiteChangeType;

    /// <summary>RA-682 AC05: how many of the request's items are charged.</summary>
    public static int ChargeableSites(AccreditationChangeRequest request) => request.Sites.Count(IsChargeable);

    /// <summary>
    /// RA-682: the request's own type, from its items: add-ORS when it adds any ORS (so it is charged), an update when
    /// it only changes ORS details.
    /// </summary>
    public static string RequestChangeType(AccreditationChangeRequest request)
    {
        // A registration-only request (RA-697) keeps its stored type, and so does a tonnage band upgrade, which has
        // no sites.
        if (request.ApplicationId is null || request.Sites.Count == 0)
            return request.ChangeType;
        if (request.Sites.All(s => s.IsUpdate))
            return AccreditationChangeRequest.UpdateOverseasSiteChangeType;
        // An update-only request that was reopened and then had an ORS added is an add-ORS request now.
        return request.ChangeType == AccreditationChangeRequest.UpdateOverseasSiteChangeType
            ? AccreditationChangeRequest.AddOverseasSiteChangeType
            : request.ChangeType;
    }

    /// <summary>
    /// RA-682: the order a request's items go to Case Management in: one submission per change type, adds first. The
    /// adapter sends them in this order, so when a submission fails the groups after it were never sent.
    /// </summary>
    public static List<IGrouping<string, ChangeRequestSite>> SubmissionGroups(AccreditationChangeRequest request) =>
        [
            .. request
                .Sites.GroupBy(s => s.ChangeType)
                .OrderBy(g => g.Key == AccreditationChangeRequest.AddOverseasSiteChangeType ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.Ordinal),
        ];

    public static List<string> MissingFields(OverseasSiteModel? site, ChangeRequestSite requestSite)
    {
        if (requestSite.IsUpdate)
            return UpdateOverseasSiteRules.MissingFields(site, requestSite);

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

    /// <summary>
    /// RA-698: the query on the item, and while it is open what the operator may change: the editable and locked
    /// fields, the BES files held before the query, and each interim site's effective values with its own
    /// editable and locked fields. Null for an item that was never queried.
    /// </summary>
    private static QueryDetailsView? QueryDetails(ChangeRequestSite item, OverseasSiteModel? site)
    {
        if (item.Query is not { } query)
            return null;
        var open = item.Status == ChangeItemStatus.Queried;
        var interimSites = open
            ? QueriedItemRules
                .ActiveInterimSites(site)
                .Select(interim =>
                {
                    var answers = item.Answers.InterimSites.Find(a => a.SiteId == interim.SiteId);
                    string? Pick(QueriedItemRules.Field<ChangeRequestInterimSiteAnswers, InterimSiteModel> field) =>
                        (answers is null ? null : field.Get(answers)) ?? field.Base(interim);
                    var values = QueriedItemRules.InterimFields.ToDictionary(f => f.Key, Pick);
                    return new InterimSiteEditView(
                        interim.SiteId,
                        interim.SiteNumber,
                        interim.Country,
                        interim.OperationCodes,
                        values,
                        QueriedItemRules.EditableInterimFields,
                        QueriedItemRules.LockedInterimFields
                    );
                })
                .ToList()
            : [];
        return new QueryDetailsView(
            query.QueryNote,
            query.RaisedAt,
            query.QuerySubmissions,
            open ? QueriedItemRules.EditableOrsFields : [],
            open ? QueriedItemRules.LockedOrsFields : [],
            query.BesFileIdsAtQuery,
            interimSites
        );
    }

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
                    // RA-682 AC02: an update never needs new BES evidence, though it may add some.
                    !requestSite.IsUpdate && IsBesEvidenceRequired(site, effective),
                    requestSite.BesEvidence,
                    missing,
                    missing.Count == 0,
                    requestSite.ItemId,
                    requestSite.ChangeType,
                    requestSite.Status,
                    requestSite.ValidFrom,
                    requestSite.WithdrawnAt,
                    requestSite.CaseManagementReference,
                    requestSite.Previous,
                    requestSite.Proposed,
                    requestSite.InterimSiteChanges,
                                        IsBesEvidenceRequired(site, effective) && !request.IsFixed(requestSite),
                    QueryDetails(requestSite, site)
                );
            })
            .ToList();

        return new ChangeRequestView(
            request.RequestId,
            RequestChangeType(request),
            request.Status,
            request.CreatedAt,
            request.UpdatedAt,
            request.SubmittedAt,
            request.SubmittedBy,
            sites,
            sites.Count > 0 && sites.All(s => s.IsComplete),
            Charge(ChargeableSites(request)),
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
    string? CaseManagementReference,
    // RA-682: an update's details as they were and as proposed, and its interim site changes. Null and empty on an add.
    OverseasSiteDetails? Previous = null,
    OverseasSiteDetails? Proposed = null,
    List<InterimSiteChange>? InterimSiteChanges = null,
    // RA-682: whether BES evidence files can be uploaded to the item now: its ORS takes BES evidence (outside the EU
    // and OECD, conditions of export not met) and Case Management can't hold the item. On an add or an update.
    bool BesEvidenceUploadAllowed = false,
    QueryDetailsView? Query = null
);

/// <summary>
/// RA-698: the regulator's query on an item. <see cref="EditableFields"/>, <see cref="LockedFields"/> and
/// <see cref="InterimSites"/> are empty once the query is answered.
/// </summary>
public sealed record QueryDetailsView(
    string QueryNote,
    DateTime RaisedAt,
    List<QuerySubmission> QuerySubmissions,
    IReadOnlyList<string> EditableFields,
    IReadOnlyList<string> LockedFields,
    List<string> PreQueryBesFileIds,
    List<InterimSiteEditView> InterimSites
);

/// <summary>RA-698: an interim site of a queried ORS: its effective values by field, and what may be changed.</summary>
public sealed record InterimSiteEditView(
    int SiteId,
    string SiteNumber,
    string Country,
    List<string> OperationCodes,
    Dictionary<string, string?> Values,
    IReadOnlyList<string> EditableFields,
    IReadOnlyList<string> LockedFields
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
    List<string> PendingOrsIds,
    bool HasOpenQuery = false
);
