using System.Text.Json;

namespace EprRegisterEnrolBackend.AdminApi.OperatorJourney;

/// <summary>RA-753: a checked Operator journey search: <c>q</c>, the fields it searches, and each filter's values.</summary>
public sealed class OperatorJourneySearch(
    string? text,
    IReadOnlyCollection<string> searchFields,
    IReadOnlyDictionary<string, IReadOnlyList<string>> filters
)
{
    /// <summary>The search field that matches a record's id exactly, which every type has.</summary>
    public const string IdField = "id";

    /// <summary><c>q</c>, trimmed; null when none was given.</summary>
    public string? Text { get; } = text;

    /// <summary>Whether <c>q</c> searches <paramref name="field"/>: never without <c>q</c>; every field when none was named.</summary>
    public bool Searches(string field) =>
        Text is not null && (searchFields.Count == 0 || searchFields.Contains(field));

    /// <summary>The values given for <paramref name="filter"/>, already checked; empty when it was not given.</summary>
    public IReadOnlyList<string> Values(string filter) =>
        filters.TryGetValue(filter, out var values) ? values : [];

    public IReadOnlyList<TEnum> Values<TEnum>(string filter)
        where TEnum : struct, Enum => Values(filter).Select(Enum.Parse<TEnum>).Distinct().ToList();

    public IReadOnlyList<int> WholeNumbers(string filter) =>
        Values(filter).Select(int.Parse).Distinct().ToList();
}

/// <summary>One filter a record type takes: its query parameter, its error code, and the values it accepts.</summary>
public sealed record SearchFilter(string Name, string ErrorCode, string Rule, Func<string, bool> Accepts)
{
    public const int MinYear = 2000;
    private const int MaxTextLength = 100;

    public static SearchFilter OneOf(string name, string errorCode, IReadOnlyList<string> values) =>
        new(
            name,
            errorCode,
            $"{name} must each be one of {string.Join(", ", values)}.",
            value => values.Contains(value, StringComparer.Ordinal)
        );

    /// <summary>An enum's names; Enum.TryParse alone would also take a number.</summary>
    public static SearchFilter EnumNames<TEnum>(string name, string errorCode)
        where TEnum : struct, Enum => OneOf(name, errorCode, Enum.GetNames<TEnum>());

    /// <summary>An enum's JSON names, which is how the result rows send it, such as <c>up_to_500</c>.</summary>
    public static SearchFilter JsonNames<TEnum>(string name, string errorCode)
        where TEnum : struct, Enum => OneOf(name, errorCode, JsonNamesOf<TEnum>().Keys.ToList());

    public static SearchFilter Year(string name = "year") =>
        new(
            name,
            "invalid_year",
            $"{name} must each be a whole number from {MinYear}, for example 2027.",
            value => AdminApiEndpoints.TryParseWholeNumber(value, out var year) && year >= MinYear
        );

    /// <summary>A stored value matched exactly, such as a country.</summary>
    public static SearchFilter ExactText(string name, string errorCode) =>
        new(
            name,
            errorCode,
            $"{name} must each be 1 to {MaxTextLength} characters, with no control characters.",
            value => value.Length is > 0 and <= MaxTextLength && !value.Any(char.IsControl)
        );

    public static SearchFilter TrueOrFalse(string name, string errorCode) =>
        OneOf(name, errorCode, ["true", "false"]);

    /// <summary>Each value of <typeparamref name="TEnum"/> by the name the JSON serialiser gives it.</summary>
    public static IReadOnlyDictionary<string, TEnum> JsonNamesOf<TEnum>()
        where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().ToDictionary(value => JsonSerializer.Serialize(value).Trim('"'), StringComparer.Ordinal);
}
