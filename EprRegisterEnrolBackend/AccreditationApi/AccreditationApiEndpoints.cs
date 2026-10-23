using System.Globalization;
using System.Text.RegularExpressions;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApi;

/// <summary>
/// The read-only accreditation API for service clients such as ReEx: an accreditation for an
/// organisation's registration in a year, and the overseas sites on it. Reachable only with the
/// <see cref="ServiceClientPolicies.Read"/> policy (client-id plus HMAC auth, one credential per
/// client). Paths follow the draft OpenAPI spec (docs/openapi), under <c>/v1</c>.
/// </summary>
public static partial class AccreditationApiEndpoints
{
    private const string AccreditationRoute =
        "/v1/organisations/{organisationNumber}/registrations/{registrationNumber}/accreditations/{year}";

    private const int DefaultLimit = 20;
    private const int MaxLimit = 100;
    private const int MinSearchYear = 2000;

    public static void UseAccreditationApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(string.Empty)
            .RequireAuthorization(ServiceClientPolicies.Read)
            .AddEndpointFilter<ServiceClientAccessLogFilter>()
            .WithTags("Service clients");

        group.MapGet("/v1/accreditations", SearchAccreditations).WithName("SearchAccreditations");
        group.MapGet(AccreditationRoute, GetAccreditation).WithName("GetAccreditation");
        group
            .MapGet($"{AccreditationRoute}/overseas-sites", ListOverseasSites)
            .WithName("ListAccreditedOverseasSites");
        group
            .MapGet($"{AccreditationRoute}/overseas-sites/{{orsId}}", GetOverseasSite)
            .WithName("GetAccreditedOverseasSite");
    }

    private static async Task<IResult> SearchAccreditations(
        HttpContext http,
        IAccreditationApplicationPersistence persistence,
        IOptions<ServiceClientsConfig> config
    )
    {
        var query = http.Request.Query;
        var paging = ParsePaging(query["pageNumber"], query["limit"]);
        if (paging.Problem is not null)
            return paging.Problem;

        int? year = null;
        if (query.TryGetValue("year", out var yearValue))
        {
            if (
                !int.TryParse(
                    yearValue.ToString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedYear
                )
                || parsedYear < MinSearchYear
            )
            {
                return new ServiceProblemResult(
                    StatusCodes.Status400BadRequest,
                    "invalid_year",
                    $"year must be a whole number from {MinSearchYear}, for example 2027."
                );
            }

            year = parsedYear;
        }

        var now = DateTimeOffset.UtcNow;
        var (items, total) = await persistence.SearchGrantedAccreditationsAsync(
            year,
            config.Value.FirstAccreditationYear,
            (paging.PageNumber - 1) * paging.Limit,
            paging.Limit
        );

        return Results.Ok(
            new PageDto<AccreditationSummaryDto>
            {
                PageNumber = paging.PageNumber,
                Limit = paging.Limit,
                TotalItems = (int)total,
                Items = items.Select(a => AccreditationApiMapper.ToSummary(a, now)).ToList(),
            }
        );
    }

    private static async Task<IResult> GetAccreditation(
        string organisationNumber,
        string registrationNumber,
        string year,
        IAccreditationApplicationPersistence persistence,
        IOptions<ServiceClientsConfig> config
    )
    {
        var lookup = await Resolve(
            organisationNumber,
            registrationNumber,
            year,
            persistence,
            config
        );
        return lookup.Problem
            ?? Results.Ok(AccreditationApiMapper.ToAccreditation(lookup.Application!, DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> ListOverseasSites(
        string organisationNumber,
        string registrationNumber,
        string year,
        HttpContext http,
        IAccreditationApplicationPersistence persistence,
        IOptions<ServiceClientsConfig> config
    )
    {
        var query = http.Request.Query;
        var paging = ParsePaging(query["pageNumber"], query["limit"]);
        if (paging.Problem is not null)
            return paging.Problem;

        var withdrawn = ParseIncludeWithdrawn(query["includeWithdrawn"]);
        if (withdrawn.Problem is not null)
            return withdrawn.Problem;

        var lookup = await Resolve(
            organisationNumber,
            registrationNumber,
            year,
            persistence,
            config
        );
        if (lookup.Problem is not null)
            return lookup.Problem;

        var application = lookup.Application!;
        var now = DateTimeOffset.UtcNow;
        var sites = AccreditedSites(application)
            .OrderBy(site => site.OrsId, StringComparer.Ordinal)
            .ThenBy(site => site.SiteId)
            .ToList();

        return Results.Ok(
            new PageDto<AccreditedOverseasSiteDto>
            {
                PageNumber = paging.PageNumber,
                Limit = paging.Limit,
                TotalItems = sites.Count,
                Items = sites
                    .Skip((paging.PageNumber - 1) * paging.Limit)
                    .Take(paging.Limit)
                    .Select(site =>
                        AccreditationApiMapper.ToOverseasSite(
                            application,
                            site,
                            withdrawn.Include,
                            now
                        )
                    )
                    .ToList(),
            }
        );
    }

    private static async Task<IResult> GetOverseasSite(
        string organisationNumber,
        string registrationNumber,
        string year,
        string orsId,
        HttpContext http,
        IAccreditationApplicationPersistence persistence,
        IOptions<ServiceClientsConfig> config
    )
    {
        if (!OrsIdPattern().IsMatch(orsId))
        {
            return new ServiceProblemResult(
                StatusCodes.Status400BadRequest,
                "invalid_ors_id",
                "orsId must be three digits, for example 001."
            );
        }

        var withdrawn = ParseIncludeWithdrawn(http.Request.Query["includeWithdrawn"]);
        if (withdrawn.Problem is not null)
            return withdrawn.Problem;

        var lookup = await Resolve(
            organisationNumber,
            registrationNumber,
            year,
            persistence,
            config
        );
        if (lookup.Problem is not null)
            return lookup.Problem;

        var application = lookup.Application!;
        var site = AccreditedSites(application).FirstOrDefault(s =>
            string.Equals(s.OrsId, orsId, StringComparison.Ordinal)
        );

        return site is null
            ? new ServiceProblemResult(
                StatusCodes.Status404NotFound,
                "overseas_site_not_found",
                $"No overseas site {orsId} was found on the {year} accreditation of registration {registrationNumber}."
            )
            : Results.Ok(
                AccreditationApiMapper.ToOverseasSite(
                    application,
                    site,
                    withdrawn.Include,
                    DateTimeOffset.UtcNow
                )
            );
    }

    // Only the sites included in the accreditation. A site the organisation registered but did
    // not select for it is not on the accreditation, so it is not served.
    private static IEnumerable<OverseasSiteModel> AccreditedSites(
        AccreditationApplicationModel application
    ) => (application.OverseasSites?.Sites ?? []).Where(site => site.Selected);

    private sealed record Withdrawn(bool Include, IResult? Problem);

    private static Withdrawn ParseIncludeWithdrawn(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return new Withdrawn(false, null);

        return bool.TryParse(value, out var include)
            ? new Withdrawn(include, null)
            : new Withdrawn(
                false,
                new ServiceProblemResult(
                    StatusCodes.Status400BadRequest,
                    "invalid_include_withdrawn",
                    "includeWithdrawn must be true or false."
                )
            );
    }

    private sealed record Lookup(AccreditationApplicationModel? Application, IResult? Problem);

    // Validates the path, then finds the granted accreditation. Anything that does not identify
    // one (an organisation, registration or year with none granted) is the same 404: the
    // accreditation does not exist as far as a service client is concerned.
    private static async Task<Lookup> Resolve(
        string organisationNumber,
        string registrationNumber,
        string year,
        IAccreditationApplicationPersistence persistence,
        IOptions<ServiceClientsConfig> config
    )
    {
        if (
            !int.TryParse(
                organisationNumber,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var orgId
            )
        )
        {
            return Bad(
                "invalid_organisation_number",
                "organisationNumber must be a whole number, for example 500027."
            );
        }

        if (!RegistrationNumberPattern().IsMatch(registrationNumber))
        {
            return Bad(
                "invalid_registration_number",
                "registrationNumber must be letters, digits, hyphens or underscores, up to 40 characters."
            );
        }

        if (
            year.Length != 4
            || !int.TryParse(
                year,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var accreditationYear
            )
        )
        {
            return Bad("invalid_year", "year must be a four digit year, for example 2027.");
        }

        var application = await persistence.GetGrantedAccreditationAsync(
            orgId,
            registrationNumber,
            accreditationYear,
            config.Value.FirstAccreditationYear
        );

        return application is null
            ? new Lookup(
                null,
                new ServiceProblemResult(
                    StatusCodes.Status404NotFound,
                    "accreditation_not_found",
                    $"No granted accreditation was found for organisation {orgId}, registration {registrationNumber} in {accreditationYear}."
                )
            )
            : new Lookup(application, null);
    }

    private static Lookup Bad(string code, string detail) =>
        new(null, new ServiceProblemResult(StatusCodes.Status400BadRequest, code, detail));

    private sealed record Paging(int PageNumber, int Limit, IResult? Problem);

    private static Paging ParsePaging(string? pageNumber, string? limit)
    {
        var page = 1;
        var size = DefaultLimit;

        if (
            !string.IsNullOrEmpty(pageNumber)
            && (
                !int.TryParse(pageNumber, NumberStyles.None, CultureInfo.InvariantCulture, out page)
                || page < 1
            )
        )
        {
            return Invalid("pageNumber must be a whole number from 1.");
        }

        if (
            !string.IsNullOrEmpty(limit)
            && (
                !int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out size)
                || size < 1
                || size > MaxLimit
            )
        )
        {
            return Invalid($"limit must be a whole number from 1 to {MaxLimit}.");
        }

        return new Paging(page, size, null);

        static Paging Invalid(string detail) =>
            new(
                0,
                0,
                new ServiceProblemResult(StatusCodes.Status400BadRequest, "invalid_paging", detail)
            );
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex RegistrationNumberPattern();

    [GeneratedRegex(@"^\d{3}$")]
    private static partial Regex OrsIdPattern();
}
