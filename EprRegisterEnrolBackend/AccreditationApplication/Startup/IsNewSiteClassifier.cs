using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-794: the per-site verdict. The bucket names are the ones management-be's
// ReAccreditationIsNewSiteAudit and docs/diagnostics/ra292-isnewsite-audit.md use, so a figure
// from either service means the same thing.
public enum IsNewSiteVerdict
{
    // isNewSite is false: nothing to correct.
    NotFlaggedNew,

    // ALREADY-CORRECT: isNewSite is true and the site carries an OrsId, so it was operator-added.
    AlreadyCorrect,

    // PROVABLY-CORRUPT: no OrsId, not promoted, and ReEx holds this site.
    ProvablyCorrupt,

    // PROMOTED-CORRECTABLE: no OrsId, promoted (RegisteredNowAccredited), and ReEx holds this site.
    PromotedCorrectable,

    // AMBIGUOUS-REFUSED: no OrsId and no positive proof the site came from ReEx. Never written.
    AmbiguousRefused,
}

public sealed record IsNewSiteClassification(
    IsNewSiteVerdict Verdict,
    IReadOnlyList<string> RefusedBecause
)
{
    public bool IsCorrectable =>
        Verdict is IsNewSiteVerdict.ProvablyCorrupt or IsNewSiteVerdict.PromotedCorrectable;
}

// What ReEx says about an application's registration: the overseas sites it holds, or why they
// could not be read. Exactly one of the two is set.
public sealed record ReExSiteLookup(IReadOnlyList<OverseasSiteModel>? Sites, string? Failure)
{
    public static ReExSiteLookup Found(IReadOnlyList<OverseasSiteModel> sites) => new(sites, null);

    public static ReExSiteLookup Failed(string failure) => new(null, failure);
}

// RA-794: decides whether an overseas site's isNewSite: true is the wrongly-defaulted value.
//
// The defect: OverseasSiteModel.IsNewSite carried a `= true` initializer from 423d27d (2026-07-26)
// until RA-292 (26218ca, 2026-08-10). A missing BSON element keeps the initializer, so every site
// stored before then read back true, and the first save in the window wrote true for good.
//
// The rule is "never write false without positive proof the site came from ReEx":
//   1. isNewSite false                          -> nothing to do.
//   2. OrsId present                            -> ALREADY-CORRECT. Operator-added sites always
//      carry one (AddOverseasSiteRequest required it; since RA-482 the server allocates it), and
//      ReEx-seeded sites carry none unless seeded after RA-507 (67a70fb, 2026-08-27) - after the
//      window, when the seed set isNewSite false explicitly. No path writes an OrsId onto a site
//      already stored: OverseasSiteMerge restores the persisted one.
//   3. No OrsId: positive provenance is required. Until this change PATCH .../overseas-sites
//      stored any unknown SiteId as a new site with whatever OrsId the client sent, so "no OrsId"
//      alone does not prove ReEx origin. The proof is ReEx itself: the registration's ORS list
//      (the same GetAccreditationAsync call the seed made) holds a site with this SiteId - the
//      seed derives SiteId from ReEx's ORS key - and the same name, either the current one or
//      one on the site's edit/promotion undo stack (PreviousSites). Then it is PROVABLY-CORRUPT,
//      or PROMOTED-CORRECTABLE when RegisteredNowAccredited is set.
//   4. Otherwise AMBIGUOUS-REFUSED, naming why: ReEx did not hold it, or ReEx could not be read.
//      The operator-entered detail fields it carries are listed too, for adjudication.
//
// Unlike management-be, operator-entered detail does not block a ReEx-confirmed site: ReEx holding
// the ORS under this id and name is direct proof it is not new, and the detail is explained by
// UpdateOverseasSite (RA-470) or promotion. Management-be has no ReEx call and no PreviousSites (it
// is [JsonIgnore] and never sent), so it falls back on the detail tell.
public static class IsNewSiteClassifier
{
    public const string ProvablyCorrupt = "PROVABLY-CORRUPT";
    public const string PromotedCorrectable = "PROMOTED-CORRECTABLE";
    public const string AmbiguousRefused = "AMBIGUOUS-REFUSED";
    public const string AlreadyCorrect = "ALREADY-CORRECT";
    public const string NotFlaggedNew = "NOT-FLAGGED-NEW";

    public const string NotInReEx = "notInReEx";

    public static string BucketName(IsNewSiteVerdict verdict) =>
        verdict switch
        {
            IsNewSiteVerdict.ProvablyCorrupt => ProvablyCorrupt,
            IsNewSiteVerdict.PromotedCorrectable => PromotedCorrectable,
            IsNewSiteVerdict.AmbiguousRefused => AmbiguousRefused,
            IsNewSiteVerdict.AlreadyCorrect => AlreadyCorrect,
            IsNewSiteVerdict.NotFlaggedNew => NotFlaggedNew,
            _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
        };

    // True when the site needs ReEx's word before it can be classified - the only case the scan
    // calls ReEx for.
    public static bool NeedsReEx(OverseasSiteModel site) =>
        site.IsNewSite && string.IsNullOrWhiteSpace(site.OrsId);

    public static IsNewSiteClassification Classify(OverseasSiteModel site, ReExSiteLookup? reEx)
    {
        ArgumentNullException.ThrowIfNull(site);

        if (!site.IsNewSite)
            return new(IsNewSiteVerdict.NotFlaggedNew, []);
        if (!string.IsNullOrWhiteSpace(site.OrsId))
            return new(IsNewSiteVerdict.AlreadyCorrect, []);

        if (reEx is null)
            throw new ArgumentNullException(
                nameof(reEx),
                "A flagged site with no OrsId cannot be classified without ReEx's site list."
            );

        if (reEx.Failure is { } failure)
            return Refused(site, $"reExUnavailable ({failure})");
        if (!ReExHolds(site, reEx.Sites!))
            return Refused(site, NotInReEx);

        return new(
            site.RegisteredNowAccredited
                ? IsNewSiteVerdict.PromotedCorrectable
                : IsNewSiteVerdict.ProvablyCorrupt,
            []
        );
    }

    private static bool ReExHolds(OverseasSiteModel site, IReadOnlyList<OverseasSiteModel> reEx)
    {
        var names = site
            .PreviousSites.Select(p => p.SiteName)
            .Append(site.SiteName)
            .Select(Normalise)
            .Where(n => n.Length > 0)
            .ToHashSet();
        return reEx.Any(r => r.SiteId == site.SiteId && names.Contains(Normalise(r.SiteName)));
    }

    private static string Normalise(string? name) => (name ?? "").Trim().ToUpperInvariant();

    private static IsNewSiteClassification Refused(OverseasSiteModel site, string reason) =>
        new(IsNewSiteVerdict.AmbiguousRefused, [reason, .. OperatorDetailFields(site)]);

    // Field names only, never values: what an adjudicator checks against the operator journey.
    private static IEnumerable<string> OperatorDetailFields(OverseasSiteModel site)
    {
        if (site.ContactName is not null)
            yield return "contactName";
        if (site.ContactEmail is not null)
            yield return "contactEmail";
        if (site.ContactPhone is not null)
            yield return "contactPhone";
        if (site.OperationCodes.Count > 0)
            yield return "operationCodes";
        if (site.Code1 is not null)
            yield return "code1";
        if (site.Code2 is not null)
            yield return "code2";
        if (site.Code3 is not null)
            yield return "code3";
        if (site.AddressLine1 is not null)
            yield return "addressLine1";
        if (site.AddressLine2 is not null)
            yield return "addressLine2";
        if (site.TownOrCity is not null)
            yield return "townOrCity";
        if (site.Coordinates is not null)
            yield return "coordinates";
        if (site.RepatriatedLoads is not null)
            yield return "repatriatedLoads";
        if (site.ConditionsOfExport is not null)
            yield return "conditionsOfExport";
    }
}
