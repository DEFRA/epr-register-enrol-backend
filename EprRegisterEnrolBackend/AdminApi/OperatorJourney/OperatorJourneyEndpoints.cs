using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.Auth;

namespace EprRegisterEnrolBackend.AdminApi.OperatorJourney;

/// <summary>
/// RA-753: the Operator journey search, one endpoint per record type over the domain-model collections,
/// beside the Legacy search (<c>GET /admin/accreditation-applications</c>) and following it: <c>q</c> with
/// <c>searchFields</c>, repeatable filters, <c>pageNumber</c> and <c>limit</c>, most recently changed
/// first, the same page envelope and errors. Mapped on the admin group, so it has the admin client policy,
/// access log, rate limit and query time limit.
/// </summary>
public static class OperatorJourneyEndpoints
{
    public const string PathPrefix = "/operator-journey";

    private const string QueryParameter = "q";
    private const string SearchFieldsParameter = "searchFields";
    private const string PageNumberParameter = "pageNumber";
    private const string LimitParameter = "limit";

    private static readonly string[] CommonParameters =
    [
        QueryParameter,
        SearchFieldsParameter,
        PageNumberParameter,
        LimitParameter,
    ];

    internal static void MapOperatorJourneyEndpoints(this RouteGroupBuilder admin)
    {
        foreach (var recordType in OperatorJourneyRecordTypes.All)
        {
            admin
                .MapGet(
                    $"{PathPrefix}/{recordType.Path}",
                    (
                        HttpContext http,
                        OperatorJourneyStore store,
                        ILoggerFactory loggerFactory,
                        CancellationToken cancellationToken
                    ) => Search(recordType, http, store, loggerFactory, cancellationToken)
                )
                .WithName(recordType.EndpointName);
        }

        admin
            .MapGet($"{PathPrefix}/overseas-site-countries", GetOverseasSiteCountries)
            .WithName("GetOperatorJourneyOverseasSiteCountries");
    }

    private static async Task<IResult> Search(
        OperatorJourneyRecordType recordType,
        HttpContext http,
        OperatorJourneyStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        var query = http.Request.Query;
        if (ParseParameterNames(query, [.. CommonParameters, .. recordType.Filters.Select(f => f.Name)]) is { } unknown)
            return unknown;
        if (AdminApiEndpoints.ParseText(query, out var text) is { } badText)
            return badText;
        if (ParseSearchFields(query, recordType, out var fields) is { } badFields)
            return badFields;
        if (ParseFilters(query, recordType, out var filters) is { } badFilter)
            return badFilter;
        if (AdminApiEndpoints.ParsePaging(query, out var pageNumber, out var limit) is { } badPaging)
            return badPaging;

        try
        {
            var (items, total) = await recordType.SearchAsync(
                new OperatorJourneySearch(text, fields, filters),
                store,
                (long)(pageNumber - 1) * limit,
                limit,
                cancellationToken
            );
            return Results.Ok(
                new PageDto<object>
                {
                    PageNumber = pageNumber,
                    Limit = limit,
                    TotalItems = (int)total,
                    Items = items,
                }
            );
        }
        catch (OperatorJourneyLinkMissingException ex)
        {
            loggerFactory
                .CreateLogger(typeof(OperatorJourneyEndpoints).FullName!)
                .LogError(
                    ex,
                    "Admin Operator journey search ({RecordTypePath}): {RecordType} {RecordId} links to {Link} {LinkedId}, which does not exist",
                    recordType.Path,
                    ex.RecordType,
                    ex.RecordId.ToString(),
                    ex.Link,
                    ex.LinkedId
                );
            return new ServiceProblemResult(
                StatusCodes.Status500InternalServerError,
                "invalid_stored_record",
                $"A {ex.RecordType} in the results links to a {ex.Link} that does not exist. Quote the traceId to the delivery team."
            );
        }
    }

    private static async Task<IResult> GetOverseasSiteCountries(
        HttpContext http,
        OperatorJourneyStore store,
        CancellationToken cancellationToken
    )
    {
        if (ParseParameterNames(http.Request.Query, []) is { } unknown)
            return unknown;
        return Results.Ok(new OverseasSiteCountriesDto(await store.OverseasSiteCountriesAsync(cancellationToken)));
    }

    // A parameter the endpoint doesn't take is refused, not ignored, so a filter is never silently dropped.
    private static ServiceProblemResult? ParseParameterNames(IQueryCollection query, IReadOnlyCollection<string> allowed)
    {
        var unknown = query.Keys.FirstOrDefault(key => !allowed.Contains(key, StringComparer.Ordinal));
        if (unknown is null)
            return null;
        return AdminApiEndpoints.Bad(
            "unknown_parameter",
            allowed.Count == 0
                ? $"{unknown} is not a parameter here: this endpoint takes none."
                : $"{unknown} is not a parameter of this search. It takes {string.Join(", ", allowed)}."
        );
    }

    private static ServiceProblemResult? ParseSearchFields(
        IQueryCollection query,
        OperatorJourneyRecordType recordType,
        out List<string> fields
    )
    {
        fields = [];
        foreach (var value in query[SearchFieldsParameter])
        {
            if (value is null || !recordType.SearchFields.Contains(value, StringComparer.Ordinal))
            {
                return AdminApiEndpoints.Bad(
                    "invalid_search_fields",
                    $"searchFields must each be one of {string.Join(", ", recordType.SearchFields)}."
                );
            }

            fields.Add(value);
        }

        return null;
    }

    private static ServiceProblemResult? ParseFilters(
        IQueryCollection query,
        OperatorJourneyRecordType recordType,
        out Dictionary<string, IReadOnlyList<string>> filters
    )
    {
        filters = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var filter in recordType.Filters)
        {
            var values = query[filter.Name];
            if (values.Count == 0)
                continue;
            if (values.Any(value => value is null || !filter.Accepts(value)))
                return AdminApiEndpoints.Bad(filter.ErrorCode, filter.Rule);
            filters[filter.Name] = values.Select(value => value!).ToList();
        }

        return null;
    }
}
