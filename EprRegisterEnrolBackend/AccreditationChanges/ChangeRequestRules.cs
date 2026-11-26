using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentValidation.Results;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-492 rules for an add-overseas-site change request: which ORS fields are locked, what is
/// still missing (AC02), whether BES evidence is needed, and the charge (AC03/AC04).
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
                "This overseas reprocessing site is no longer accredited, so its details can't be changed. "
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
    /// RA-682: an ORS's editable details, as an update item snapshots them. Every value is the ORS's own, so an
    /// unchanged update's proposed details equal these.
    /// </summary>
    public static OverseasSiteDetails DetailsOf(OverseasSiteModel site) =>
        new()
        {
            SiteName = site.SiteName,
            AddressLine1 = site.AddressLine1,
            AddressLine2 = site.AddressLine2,
            TownOrCity = site.TownOrCity,
            ContactName = site.ContactName,
            ContactEmail = site.ContactEmail,
            ContactPhone = site.ContactPhone,
            RepatriatedLoads = site.RepatriatedLoads,
        };

    /// <summary>RA-682: whether an update item changes anything: its ORS's details, or an interim site.</summary>
    public static bool HasChanges(ChangeRequestSite requestSite) =>
        requestSite.InterimSiteChanges.Count > 0 || requestSite.Proposed != requestSite.Previous;

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

    // RA-682 AC02: a locked value may be sent back as the form shows it, but not changed. A blank one matches a stored
    // value that is blank or missing, and both sides are trimmed: the form can't tell them apart.
    private static bool SameText(string? sent, string? stored) =>
        (sent?.Trim() ?? "") == (stored?.Trim() ?? "");

    private static bool SameCodes(IEnumerable<string>? sent, IEnumerable<string>? stored) =>
        (sent ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(stored ?? []);

    private static readonly string[] s_lockedOrsFields = [Country, Coordinates, OperationCodes, ConditionsOfExport];

    private static readonly string[] s_lockedInterimFields =
    [
        nameof(AddInterimSiteRequest.Country),
        nameof(AddInterimSiteRequest.OperationCodes),
    ];

    private const string LockedOrsFieldMessage = "can't be changed for an accredited overseas site.";

    /// <summary>
    /// RA-682 AC02: validation errors with those on an update's locked ORS fields left out. The form echoes the locked
    /// values back from read-only inputs, blank when the ORS has none, so they are checked against the ORS instead
    /// (<see cref="ChangedLockedOrsFields"/>).
    /// </summary>
    public static Dictionary<string, string[]> WithoutLockedOrsFieldErrors(ValidationResult validation) =>
        WithoutErrorsOn(validation, s_lockedOrsFields);

    /// <summary>
    /// RA-682 AC04: validation errors with those on a changed interim site's country and recycling operations left
    /// out. They stay as they are (<see cref="ChangedLockedInterimFields"/>), so the create rules don't apply: an
    /// interim site from before RA-486 has no recycling operations, which the create rules would refuse.
    /// </summary>
    public static Dictionary<string, string[]> WithoutLockedInterimFieldErrors(ValidationResult validation) =>
        WithoutErrorsOn(validation, s_lockedInterimFields);

    // A list's rules report each element as "Field[0]", so a field's errors are matched on its name or that prefix.
    private static Dictionary<string, string[]> WithoutErrorsOn(ValidationResult validation, string[] fields) =>
        validation
            .Errors.Where(e =>
                !fields.Any(f =>
                    e.PropertyName.Equals(f, StringComparison.OrdinalIgnoreCase)
                    || e.PropertyName.StartsWith(f + "[", StringComparison.OrdinalIgnoreCase)
                )
            )
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

    /// <summary>
    /// RA-682 AC02: the locked fields an update's PATCH would change on the accredited ORS, as validation errors.
    /// Basel codes and BES aren't in the body at all, so they can't be changed either.
    /// </summary>
    public static Dictionary<string, string[]> ChangedLockedOrsFields(
        OverseasSiteModel site,
        PatchChangeRequestSiteRequest body
    )
    {
        var changed = new Dictionary<string, string[]>();
        void Locked(string field, bool isChanged)
        {
            if (isChanged)
                changed[field] = [$"{field} {LockedOrsFieldMessage}"];
        }
        Locked(Country, body.Country is not null && !SameText(body.Country, site.Country));
        Locked(Coordinates, body.Coordinates is not null && !SameText(body.Coordinates, site.Coordinates));
        Locked(OperationCodes, body.OperationCodes is not null && !SameCodes(body.OperationCodes, site.OperationCodes));
        Locked(
            ConditionsOfExport,
            body.ConditionsOfExport is { } conditions && conditions != site.ConditionsOfExport
        );
        return changed;
    }

    /// <summary>
    /// RA-682: an update's proposed details with a PATCH's details applied. Absent means unchanged; an optional line
    /// (address line 2, phone) is cleared with a blank value.
    /// </summary>
    public static OverseasSiteDetails ApplyDetails(OverseasSiteDetails proposed, PatchChangeRequestSiteRequest body) =>
        proposed with
        {
            SiteName = body.SiteName?.Trim() ?? proposed.SiteName,
            AddressLine1 = body.AddressLine1?.Trim() ?? proposed.AddressLine1,
            AddressLine2 = body.AddressLine2 is { } line2 ? NullIfBlank(line2) : proposed.AddressLine2,
            TownOrCity = body.TownOrCity?.Trim() ?? proposed.TownOrCity,
            ContactName = body.ContactName?.Trim() ?? proposed.ContactName,
            ContactEmail = body.ContactEmail?.Trim() ?? proposed.ContactEmail,
            ContactPhone = body.ContactPhone is { } phone ? NullIfBlank(phone) : proposed.ContactPhone,
            RepatriatedLoads = body.RepatriatedLoads?.Trim() ?? proposed.RepatriatedLoads,
        };

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// RA-682: the locked fields a change to an interim site would change: its country and recycling operations stay
    /// as they are, as the ORS's own do (AC02).
    /// </summary>
    public static Dictionary<string, string[]> ChangedLockedInterimFields(
        InterimSiteModel interim,
        AddInterimSiteRequest body
    )
    {
        var changed = new Dictionary<string, string[]>();
        if (!SameText(body.Country, interim.Country))
            changed[nameof(AddInterimSiteRequest.Country)] = ["An interim site's country can't be changed."];
        if (!SameCodes(body.OperationCodes, interim.OperationCodes))
            changed[nameof(AddInterimSiteRequest.OperationCodes)] =
            [
                "An interim site's recycling operations can't be changed.",
            ];
        return changed;
    }

    /// <summary>
    /// RA-682 AC04: the update's change to one existing interim site. There is at most one per interim site: a change
    /// replaces a removal, and a change back to what the site holds drops it. The interim site keeps its own country
    /// and recycling operations, whatever the form sent back for them.
    /// </summary>
    public static void SetInterimChange(ChangeRequestSite item, InterimSiteModel interim, InterimSiteDetails changed)
    {
        item.InterimSiteChanges.RemoveAll(c => c.InterimSiteId == interim.SiteId);
        var previous = InterimSiteDetails.From(interim);
        var proposed = changed with { Country = previous.Country, OperationCodes = previous.OperationCodes };
        if (proposed != previous)
            item.InterimSiteChanges.Add(
                new InterimSiteChange
                {
                    Action = InterimSiteChange.Change,
                    InterimSiteId = interim.SiteId,
                    Previous = previous,
                    Proposed = proposed,
                }
            );
    }

    /// <summary>RA-682 AC04: the update removes one existing interim site, in place of any change to it.</summary>
    public static void SetInterimRemoval(ChangeRequestSite item, InterimSiteModel interim)
    {
        item.InterimSiteChanges.RemoveAll(c => c.InterimSiteId == interim.SiteId);
        item.InterimSiteChanges.Add(
            new InterimSiteChange
            {
                Action = InterimSiteChange.Remove,
                InterimSiteId = interim.SiteId,
                Previous = InterimSiteDetails.From(interim),
            }
        );
    }

    /// <summary>The ORS's interim site by id, active, whether it is in the list or only in the pre-RA-603 mirror.</summary>
    public static InterimSiteModel? ActiveInterimSite(OverseasSiteModel site, int interimSiteId) =>
        InterimSiteSync.Active(site).FirstOrDefault(i => i.SiteId == interimSiteId);

    /// <summary>
    /// RA-682 AC07: takes an update's previous details from the ORS as it stands at submit, so the history and Case
    /// Management see the values the change replaces. A detail the operator didn't change follows the ORS; one they did
    /// keeps their value. A change to an interim site is rebased the same way, and dropped if it no longer changes
    /// anything. Only for an item Case Management can't already hold: a held one would be replayed as first sent.
    /// </summary>
    public static void RebaseOnCurrent(ChangeRequestSite item, OverseasSiteModel site)
    {
        var current = DetailsOf(site);
        var before = item.Previous ?? current;
        var proposed = item.Proposed ?? before;
        string? Follow(string? mine, string? was, string? now) => mine == was ? now : mine;
        item.Proposed = proposed with
        {
            SiteName = Follow(proposed.SiteName, before.SiteName, current.SiteName),
            AddressLine1 = Follow(proposed.AddressLine1, before.AddressLine1, current.AddressLine1),
            AddressLine2 = Follow(proposed.AddressLine2, before.AddressLine2, current.AddressLine2),
            TownOrCity = Follow(proposed.TownOrCity, before.TownOrCity, current.TownOrCity),
            ContactName = Follow(proposed.ContactName, before.ContactName, current.ContactName),
            ContactEmail = Follow(proposed.ContactEmail, before.ContactEmail, current.ContactEmail),
            ContactPhone = Follow(proposed.ContactPhone, before.ContactPhone, current.ContactPhone),
            RepatriatedLoads = Follow(proposed.RepatriatedLoads, before.RepatriatedLoads, current.RepatriatedLoads),
        };
        item.Previous = current;
        foreach (var change in item.InterimSiteChanges.Where(c => c.InterimSiteId is not null).ToList())
        {
            if (ActiveInterimSite(site, change.InterimSiteId!.Value) is not { } interim)
                continue;
            var was = change.Previous;
            var now = InterimSiteDetails.From(interim);
            change.Previous = now;
            if (change.Action != InterimSiteChange.Change || was is null || change.Proposed is not { } mine)
                continue;
            change.Proposed = mine with
            {
                SiteName = Follow(mine.SiteName, was.SiteName, now.SiteName)!,
                AddressLine1 = Follow(mine.AddressLine1, was.AddressLine1, now.AddressLine1)!,
                AddressLine2 = Follow(mine.AddressLine2, was.AddressLine2, now.AddressLine2),
                TownOrCity = Follow(mine.TownOrCity, was.TownOrCity, now.TownOrCity)!,
                StateOrRegion = Follow(mine.StateOrRegion, was.StateOrRegion, now.StateOrRegion),
                Postcode = Follow(mine.Postcode, was.Postcode, now.Postcode),
                ContactName = Follow(mine.ContactName, was.ContactName, now.ContactName)!,
                ContactEmail = Follow(mine.ContactEmail, was.ContactEmail, now.ContactEmail)!,
                ContactPhone = Follow(mine.ContactPhone, was.ContactPhone, now.ContactPhone)!,
                Country = now.Country,
                OperationCodes = now.OperationCodes,
            };
            if (change.Proposed == now)
                item.InterimSiteChanges.Remove(change);
        }
    }

    public static List<string> MissingFields(OverseasSiteModel? site, ChangeRequestSite requestSite)
    {
        // RA-682: an update's details are validated as they are patched and its locked fields can't change, so it can
        // only be missing a change, or its ORS, if that is no longer accredited.
        if (requestSite.IsUpdate)
        {
            if (site is null || AccreditationStatus(site) != SiteAccreditationStatus.Accredited)
                return [Site];
            return HasChanges(requestSite) ? [] : [Changes];
        }

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
                    // RA-682: an update takes no BES evidence (AC02); BES upload isn't part of changing an ORS.
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
                    requestSite.InterimSiteChanges
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
    List<InterimSiteChange>? InterimSiteChanges = null
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
