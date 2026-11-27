using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentValidation.Results;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-682 rules for an update-overseas-site item, which changes an approved ORS's details and interim sites: what
/// is locked (AC02), how interim site changes are recorded (AC04), the rebase onto the ORS at submit (AC07), and
/// what an update can be missing. The rules every item shares (charging, grouping, BES evidence) stay in
/// <see cref="ChangeRequestRules"/>.
/// </summary>
public static class UpdateOverseasSiteRules
{
    /// <summary>
    /// RA-682: whether an update item changes anything: its ORS's details, an interim site, or new BES evidence.
    /// </summary>
    public static bool HasChanges(ChangeRequestSite requestSite) =>
        requestSite.InterimSiteChanges.Count > 0
        || requestSite.Proposed != requestSite.Previous
        || requestSite.BesEvidence is { BesEvidenceUploads.Count: > 0 };

    /// <summary>
    /// An update's details are validated as they are patched and its locked fields can't change, so it can only be
    /// missing a change, its ORS, if that is no longer approved, or a clean scan of new BES evidence.
    /// </summary>
    public static List<string> MissingFields(OverseasSiteModel? site, ChangeRequestSite requestSite)
    {
        if (site is null || ChangeRequestRules.AccreditationStatus(site) != SiteAccreditationStatus.Accredited)
            return [ChangeRequestRules.Site];
        if (!HasChanges(requestSite))
            return [ChangeRequestRules.Changes];
        var uploadedEvidence = requestSite.BesEvidence is { BesEvidenceUploads.Count: > 0 };
        return uploadedEvidence && !ChangeRequestRules.HasCleanBesEvidence(requestSite.BesEvidence)
            ? [ChangeRequestRules.BesEvidence]
            : [];
    }

    // RA-682 AC02: a locked value may be sent back as the form shows it, but not changed. A blank one matches a stored
    // value that is blank or missing, and both sides are trimmed: the form can't tell them apart.
    private static bool SameText(string? sent, string? stored) =>
        (sent?.Trim() ?? "") == (stored?.Trim() ?? "");

    private static bool SameCodes(IEnumerable<string>? sent, IEnumerable<string>? stored) =>
        (sent ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(stored ?? []);

    private static readonly string[] s_lockedOrsFields =
    [
        ChangeRequestRules.Country,
        ChangeRequestRules.Coordinates,
        ChangeRequestRules.OperationCodes,
        ChangeRequestRules.ConditionsOfExport,
    ];

    // RA-682's AC02 locks the ORS's own fields; locking an interim site's as well is still to be confirmed with product.
    private static readonly string[] s_lockedInterimFields =
    [
        nameof(AddInterimSiteRequest.Country),
        nameof(AddInterimSiteRequest.OperationCodes),
    ];

    private const string LockedOrsFieldMessage = "can't be changed for an approved overseas site.";

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
    /// RA-682 AC02: the locked fields an update's PATCH would change on the approved ORS, as validation errors.
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
        Locked(ChangeRequestRules.Country, body.Country is not null && !SameText(body.Country, site.Country));
        Locked(
            ChangeRequestRules.Coordinates,
            body.Coordinates is not null && !SameText(body.Coordinates, site.Coordinates)
        );
        Locked(
            ChangeRequestRules.OperationCodes,
            body.OperationCodes is not null && !SameCodes(body.OperationCodes, site.OperationCodes)
        );
        Locked(
            ChangeRequestRules.ConditionsOfExport,
            body.ConditionsOfExport is { } conditions && conditions != site.ConditionsOfExport
        );
        return changed;
    }

    /// <summary>
    /// RA-682: an update's proposed details with the details a PATCH sent applied. Absent means unchanged; an optional
    /// line (address line 2, phone) is cleared with a blank value.
    /// </summary>
    public static OverseasSiteDetails ApplyDetails(OverseasSiteDetails proposed, OverseasSiteDetails sent) =>
        OverseasSiteDetails.Fields.Aggregate(
            proposed,
            (details, field) => field.Get(sent) is { } value ? field.Set(details, field.Clean(value)) : details
        );

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
                    Action = InterimSiteChangeAction.Change,
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
                Action = InterimSiteChangeAction.Remove,
                InterimSiteId = interim.SiteId,
                Previous = InterimSiteDetails.From(interim),
            }
        );
    }

    /// <summary>The ORS's interim site by id, active, whether it is in the list or only in the pre-RA-603 mirror.</summary>
    public static InterimSiteModel? ActiveInterimSite(OverseasSiteModel site, int interimSiteId) =>
        InterimSiteSync.Active(site).FirstOrDefault(i => i.SiteId == interimSiteId);

    // A detail the operator didn't change follows the ORS; one they did keeps their value.
    private static string? Follow(string? mine, string? was, string? now) => mine == was ? now : mine;

    /// <summary>
    /// RA-682 AC07: takes an update's previous details from the ORS as it stands at submit, so the history and Case
    /// Management see the values the change replaces. A detail the operator didn't change follows the ORS; one they did
    /// keeps their value. A change to an interim site is rebased the same way, and dropped if it no longer changes
    /// anything. Only for an item Case Management can't already hold: a held one would be replayed as first sent.
    /// </summary>
    public static void RebaseOnCurrent(ChangeRequestSite item, OverseasSiteModel site)
    {
        var current = OverseasSiteDetails.From(site);
        var before = item.Previous ?? current;
        var proposed = item.Proposed ?? before;
        item.Proposed = OverseasSiteDetails.Fields.Aggregate(
            proposed,
            (details, field) => field.Set(details, Follow(field.Get(proposed), field.Get(before), field.Get(current)))
        );
        item.Previous = current;
        foreach (var change in item.InterimSiteChanges.Where(c => c.InterimSiteId is not null).ToList())
        {
            if (ActiveInterimSite(site, change.InterimSiteId!.Value) is { } interim && RebaseInterimChange(change, interim))
                item.InterimSiteChanges.Remove(change);
        }
    }

    // Rebases one interim site change on the interim site as it is now; true when it no longer changes anything.
    private static bool RebaseInterimChange(InterimSiteChange change, InterimSiteModel interim)
    {
        var was = change.Previous;
        var now = InterimSiteDetails.From(interim);
        change.Previous = now;
        if (change.Action != InterimSiteChangeAction.Change || was is null || change.Proposed is not { } mine)
            return false;
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
        return change.Proposed == now;
    }
}
