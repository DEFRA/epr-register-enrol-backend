using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentValidation;

namespace EprRegisterEnrolBackend.AccreditationChanges;

/// <summary>
/// RA-698: which fields of a queried item the operator may change, how a change is applied to the item's answers, and
/// what changed since the query was raised. The ORS's editable fields are its name, address, contact and rejected
/// loads; each interim site's are its name, address and contact. Everything else, and every other item, is read-only.
/// </summary>
public static class QueriedItemRules
{
    public const string SiteName = "siteName";
    public const string AddressLine1 = "addressLine1";
    public const string AddressLine2 = "addressLine2";
    public const string TownOrCity = "townOrCity";
    public const string StateOrRegion = "stateOrRegion";
    public const string Postcode = "postcode";
    public const string ContactName = "contactName";
    public const string ContactEmail = "contactEmail";
    public const string ContactPhone = "contactPhone";
    public const string RejectedLoadsArrangements = "rejectedLoadsArrangements";
    public const string BaselCodes = "baselCodes";

    /// <summary>One editable field: how to read it from a PATCH body and the stored answers, and what it was before.</summary>
    internal sealed record Field<TAnswers, TBase>(
        string Key,
        Func<PatchQueriedSiteRequest, string?> FromBody,
        Func<TAnswers, string?> Get,
        Action<TAnswers, string?> Set,
        Func<TBase, string?> Base,
        bool Required
    );

    internal static readonly IReadOnlyList<Field<ChangeRequestSiteAnswers, OverseasSiteModel>> OrsFields =
    [
        new(SiteName, b => b.SiteName, a => a.SiteName, (a, v) => a.SiteName = v, s => s.SiteName, true),
        new(AddressLine1, b => b.AddressLine1, a => a.AddressLine1, (a, v) => a.AddressLine1 = v, s => s.AddressLine1, true),
        new(AddressLine2, b => b.AddressLine2, a => a.AddressLine2, (a, v) => a.AddressLine2 = v, s => s.AddressLine2, false),
        new(TownOrCity, b => b.TownOrCity, a => a.TownOrCity, (a, v) => a.TownOrCity = v, s => s.TownOrCity, true),
        new(StateOrRegion, b => b.StateOrRegion, a => a.StateOrRegion, (a, v) => a.StateOrRegion = v, _ => null, false),
        new(Postcode, b => b.Postcode, a => a.Postcode, (a, v) => a.Postcode = v, _ => null, false),
        new(ContactName, b => b.ContactName, a => a.ContactName, (a, v) => a.ContactName = v, s => s.ContactName, true),
        new(ContactEmail, b => b.ContactEmail, a => a.ContactEmail, (a, v) => a.ContactEmail = v, s => s.ContactEmail, true),
        new(ContactPhone, b => b.ContactPhone, a => a.ContactPhone, (a, v) => a.ContactPhone = v, s => s.ContactPhone, false),
        new(
            RejectedLoadsArrangements,
            b => b.RejectedLoadsArrangements,
            a => a.RejectedLoadsArrangements,
            (a, v) => a.RejectedLoadsArrangements = v,
            s => s.RepatriatedLoads,
            true
        ),
    ];

    internal static readonly IReadOnlyList<Field<ChangeRequestInterimSiteAnswers, InterimSiteModel>> InterimFields =
    [
        new(SiteName, b => b.SiteName, a => a.SiteName, (a, v) => a.SiteName = v, s => s.SiteName, true),
        new(AddressLine1, b => b.AddressLine1, a => a.AddressLine1, (a, v) => a.AddressLine1 = v, s => s.AddressLine1, true),
        new(AddressLine2, b => b.AddressLine2, a => a.AddressLine2, (a, v) => a.AddressLine2 = v, s => s.AddressLine2, false),
        new(TownOrCity, b => b.TownOrCity, a => a.TownOrCity, (a, v) => a.TownOrCity = v, s => s.TownOrCity, true),
        new(StateOrRegion, b => b.StateOrRegion, a => a.StateOrRegion, (a, v) => a.StateOrRegion = v, s => s.StateOrRegion, false),
        new(Postcode, b => b.Postcode, a => a.Postcode, (a, v) => a.Postcode = v, s => s.Postcode, false),
        new(ContactName, b => b.ContactName, a => a.ContactName, (a, v) => a.ContactName = v, s => s.ContactName, true),
        new(ContactEmail, b => b.ContactEmail, a => a.ContactEmail, (a, v) => a.ContactEmail = v, s => s.ContactEmail, true),
        new(ContactPhone, b => b.ContactPhone, a => a.ContactPhone, (a, v) => a.ContactPhone = v, s => s.ContactPhone, false),
    ];

    public static readonly IReadOnlyList<string> EditableOrsFields =
    [
        .. OrsFields.Select(f => f.Key),
        ChangeRequestRules.BesEvidence,
    ];

    public static readonly IReadOnlyList<string> LockedOrsFields =
    [
        ChangeRequestRules.Country,
        ChangeRequestRules.Coordinates,
        ChangeRequestRules.OperationCodes,
        BaselCodes,
        ChangeRequestRules.ConditionsOfExport,
    ];

    public static readonly IReadOnlyList<string> EditableInterimFields = [.. InterimFields.Select(f => f.Key)];

    public static readonly IReadOnlyList<string> LockedInterimFields =
    [
        ChangeRequestRules.Country,
        ChangeRequestRules.OperationCodes,
    ];

    /// <summary>The interim sites of the ORS that can be answered: the active ones, as the ORS summary lists them.</summary>
    public static IEnumerable<InterimSiteModel> ActiveInterimSites(OverseasSiteModel? site)
    {
        if (site is null)
            return [];
        IEnumerable<InterimSiteModel> sites = site.InterimSites;
        if (site.InterimSites.Count == 0 && site.InterimSite is not null)
            sites = [site.InterimSite];
        return sites.Where(s => s.RemovedAt is null);
    }

    /// <summary>
    /// Applies the body's non-null values to the ORS's answers. A value equal to the ORS's own is a revert, so the answer
    /// is dropped rather than kept as a "change" that changes nothing.
    /// </summary>
    internal static void Apply(ChangeRequestSiteAnswers answers, PatchQueriedSiteRequest body, OverseasSiteModel site) =>
        ApplyFields(OrsFields, answers, body, site);

    internal static void Apply(
        ChangeRequestInterimSiteAnswers answers,
        PatchQueriedSiteRequest body,
        InterimSiteModel site
    ) => ApplyFields(InterimFields, answers, body, site);

    private static void ApplyFields<TAnswers, TBase>(
        IReadOnlyList<Field<TAnswers, TBase>> fields,
        TAnswers answers,
        PatchQueriedSiteRequest body,
        TBase site
    )
    {
        foreach (var field in fields)
        {
            if (field.FromBody(body)?.Trim() is not { } value)
                continue;
            field.Set(answers, value == (field.Base(site) ?? string.Empty) ? null : value);
        }
    }

    /// <summary>The locked fields a body supplies, as validation errors keyed by field. Empty when it supplies none.</summary>
    public static Dictionary<string, string[]> LockedViolations(PatchQueriedSiteRequest body, bool interim)
    {
        var supplied = new List<string>();
        if (body.Country is not null)
            supplied.Add(ChangeRequestRules.Country);
        if (body.OperationCodes is not null)
            supplied.Add(ChangeRequestRules.OperationCodes);
        if (body.Coordinates is not null)
            supplied.Add(ChangeRequestRules.Coordinates);
        if (body.BaselCodes is not null)
            supplied.Add(BaselCodes);
        if (body.ConditionsOfExport is not null)
            supplied.Add(ChangeRequestRules.ConditionsOfExport);
        // An interim site has no rejected loads of its own.
        if (interim && body.RejectedLoadsArrangements is not null)
            supplied.Add(RejectedLoadsArrangements);

        return supplied.ToDictionary(
            field => field,
            field => new[] { $"{field} cannot be changed in response to a query." }
        );
    }

    /// <summary>The answers' changed values as field keys ("siteName", "interimSites[12].contactName") to values.</summary>
    public static Dictionary<string, string?> Values(ChangeRequestSiteAnswers answers)
    {
        var values = new Dictionary<string, string?>();
        foreach (var field in OrsFields)
        {
            if (field.Get(answers) is { } value)
                values[field.Key] = value;
        }
        foreach (var interim in answers.InterimSites)
        {
            foreach (var field in InterimFields)
            {
                if (field.Get(interim) is { } value)
                    values[InterimKey(interim.SiteId, field.Key)] = value;
            }
        }
        return values;
    }

    public static string InterimKey(int siteId, string field) => $"interimSites[{siteId}].{field}";

    /// <summary>
    /// The fields whose answer differs from the answers when the query was raised. An answer undone since (back at the
    /// ORS's own value, so stored as none) is a change too: case management still holds the earlier answer.
    /// </summary>
    public static List<string> ChangedFields(ChangeRequestSiteAnswers current, ChangeRequestSiteAnswers? atQuery)
    {
        var before = Values(atQuery ?? new ChangeRequestSiteAnswers());
        var now = Values(current);
        return
        [
            .. now.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key),
            .. before.Keys.Where(key => !now.ContainsKey(key)),
        ];
    }

    /// <summary>BES files on the item that were not there when the query was raised.</summary>
    public static List<BesEvidenceFileModel> NewBesFiles(ChangeRequestSite item) =>
    [
        .. (item.BesEvidence?.BesEvidenceUploads ?? []).Where(f =>
            item.Query is null || !item.Query.BesFileIdsAtQuery.Contains(f.FileId)
        ),
    ];

    public static ChangeRequestSiteAnswers Clone(ChangeRequestSiteAnswers answers) =>
        JsonSerializer.Deserialize<ChangeRequestSiteAnswers>(JsonSerializer.Serialize(answers))!;
}

/// <summary>
/// RA-698: format rules for a queried item's editable values: the required ones cannot be blanked, and the length and
/// email rules are the ORS journey's. Whether a field may be supplied at all (the locked ones may not) is the
/// endpoint's check, which answers 422.
/// </summary>
public class PatchQueriedSiteRequestValidator : AbstractValidator<PatchQueriedSiteRequest>
{
    // The add-ORS journey's limits (OverseasSiteRequestValidatorBase), so what that journey accepts is accepted here.
    private const int MaxLength = 200;
    private const int MaxTownLength = 100;
    private const int MaxEmailLength = 254;
    private const int MaxPhoneLength = 30;
    private const int MaxRejectedLoadsLength = 5000;

    private static int LimitOf(string key) =>
        key switch
        {
            QueriedItemRules.TownOrCity => MaxTownLength,
            QueriedItemRules.ContactEmail => MaxEmailLength,
            QueriedItemRules.ContactPhone => MaxPhoneLength,
            QueriedItemRules.RejectedLoadsArrangements => MaxRejectedLoadsLength,
            _ => MaxLength,
        };

    public PatchQueriedSiteRequestValidator()
    {
        foreach (var field in QueriedItemRules.OrsFields)
        {
            var key = field.Key;
            var required = field.Required;
            RuleFor(r => field.FromBody(r))
                .NotEmpty()
                .WithMessage($"{key} must not be blank.")
                .When(r => required && field.FromBody(r) is not null)
                .OverridePropertyName(key);
            RuleFor(r => field.FromBody(r))
                .MaximumLength(LimitOf(key))
                .When(r => field.FromBody(r) is not null)
                .OverridePropertyName(key);
        }

        RuleFor(r => r.ContactEmail)
            .EmailAddress()
            .When(r => !string.IsNullOrWhiteSpace(r.ContactEmail))
            .OverridePropertyName(QueriedItemRules.ContactEmail);
    }
}

public class ResubmitItemRequestValidator : AbstractValidator<ResubmitItemRequest>
{
    public ResubmitItemRequestValidator()
    {
        RuleFor(r => r.FullName).NotEmpty().MaximumLength(MaxLength);
        RuleFor(r => r.JobTitle).NotEmpty().MaximumLength(MaxLength);
    }

    private const int MaxLength = 200;
}

public class QueryFromCaseManagementRequestValidator : AbstractValidator<QueryFromCaseManagementRequest>
{
    public QueryFromCaseManagementRequestValidator() =>
        RuleFor(r => r.Note).NotEmpty().MaximumLength(5000);
}
