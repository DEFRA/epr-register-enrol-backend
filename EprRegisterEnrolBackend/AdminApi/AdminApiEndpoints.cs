using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace EprRegisterEnrolBackend.AdminApi;

/// <summary>
/// RA-740: the read-only admin API, for the delivery team's support tool
/// (<c>epr-register-enrol-admin</c>). Paths and errors follow <c>docs/openapi/admin-api.yaml</c>.
/// Signed like the <c>/v1</c> service-client API, with the same access log, request limit and
/// query time limit, but only the admin service client may call it
/// (<see cref="ServiceClientPolicies.Admin"/>).
/// </summary>
public static class AdminApiEndpoints
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;
    private const int MaxSearchLength = 100;
    private const int MinYear = 2000;

    // The searchFields values, as named in the spec.
    private static readonly Dictionary<string, LegacyApplicationSearchField> SearchFields = new(
        StringComparer.Ordinal
    )
    {
        ["accreditationNumber"] = LegacyApplicationSearchField.AccreditationNumber,
        ["registrationNumber"] = LegacyApplicationSearchField.RegistrationNumber,
        ["organisationNumber"] = LegacyApplicationSearchField.OrganisationNumber,
        ["organisationName"] = LegacyApplicationSearchField.OrganisationName,
    };

    private static readonly string[] StatusNames = Enum.GetNames<ApplicationStatus>();

    public static void UseAdminApiEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin")
            .RequireAuthorization(ServiceClientPolicies.Admin)
            .AddEndpointFilter<ServiceClientAccessLogFilter>()
            .AddEndpointFilter<ServiceClientRateLimitFilter>()
            .AddEndpointFilter<ServiceClientQueryTimeoutFilter>()
            .WithTags("Admin");

        group
            .MapGet("/accreditation-applications", SearchLegacyAccreditationApplications)
            .WithName("SearchLegacyAccreditationApplications");
    }

    private static async Task<IResult> SearchLegacyAccreditationApplications(
        HttpContext http,
        ILegacyApplicationSearch applications,
        IOptions<ServiceClientsConfig> config,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken
    )
    {
        var query = http.Request.Query;

        var text = query["q"];
        if (text.Count > 1)
            return Bad("invalid_q", "q may be given once.");
        var q = text.ToString().Trim();
        if (q.Length > MaxSearchLength)
            return Bad("invalid_q", $"q must be at most {MaxSearchLength} characters.");
        // A Mongo regex can't hold a NUL, and no number or name holds a control character.
        if (q.Any(char.IsControl))
            return Bad("invalid_q", "q must not contain control characters.");

        var fields = new List<LegacyApplicationSearchField>();
        foreach (var value in query["searchFields"])
        {
            if (value is null || !SearchFields.TryGetValue(value, out var field))
            {
                return Bad(
                    "invalid_search_fields",
                    $"searchFields must each be one of {string.Join(", ", SearchFields.Keys)}."
                );
            }

            fields.Add(field);
        }

        var statuses = new List<ApplicationStatus>();
        foreach (var value in query["status"])
        {
            // Matched against the names only: Enum.TryParse alone would also take a number.
            if (value is null || !StatusNames.Contains(value, StringComparer.Ordinal))
            {
                return Bad(
                    "invalid_status",
                    $"status must each be one of {string.Join(", ", StatusNames)}."
                );
            }

            statuses.Add(Enum.Parse<ApplicationStatus>(value));
        }

        int? year = null;
        if (query.TryGetValue("year", out var yearValue))
        {
            if (!TryParseWholeNumber(yearValue, out var parsedYear) || parsedYear < MinYear)
                return Bad("invalid_year", $"year must be a whole number from {MinYear}, for example 2027.");
            year = parsedYear;
        }

        var pageNumber = 1;
        if (
            !StringValues.IsNullOrEmpty(query["pageNumber"])
            && (!TryParseWholeNumber(query["pageNumber"], out pageNumber) || pageNumber < 1)
        )
        {
            return Bad("invalid_page_number", "pageNumber must be a whole number from 1.");
        }

        var limit = DefaultLimit;
        if (
            !StringValues.IsNullOrEmpty(query["limit"])
            && (!TryParseWholeNumber(query["limit"], out limit) || limit < 1 || limit > MaxLimit)
        )
        {
            return Bad("invalid_limit", $"limit must be a whole number from 1 to {MaxLimit}.");
        }

        var (items, total) = await applications.SearchLegacyApplicationsAsync(
            new LegacyApplicationSearch(q.Length == 0 ? null : q, fields, statuses, year),
            (long)(pageNumber - 1) * limit,
            limit,
            config.Value.QueryTimeout,
            cancellationToken
        );

        var options = json.Value.SerializerOptions;
        return Results.Ok(
            new PageDto<JsonObject>
            {
                PageNumber = pageNumber,
                Limit = limit,
                TotalItems = (int)total,
                Items = items.Select(item => AsStored(item, options)).ToList(),
            }
        );
    }

    /// <summary>
    /// The application as the service holds it, in camelCase: the model as the other endpoints
    /// serialise it, plus the stored domain-model links those endpoints leave off, less the values
    /// derived when an operator reads an application, which are not stored.
    /// </summary>
    internal static JsonObject AsStored(
        AccreditationApplicationModel application,
        JsonSerializerOptions options
    )
    {
        var stored = JsonSerializer.SerializeToNode(application, options)!.AsObject();
        stored.Remove(JsonName(nameof(AccreditationApplicationModel.NotificationStatus), options));
        stored.Remove(JsonName(nameof(AccreditationApplicationModel.DueDate), options));
        stored[JsonName(nameof(AccreditationApplicationModel.RegistrationEntityId), options)] =
            application.RegistrationEntityId?.ToString();
        stored[JsonName(nameof(AccreditationApplicationModel.AccreditationEntityId), options)] =
            application.AccreditationEntityId?.ToString();
        return stored;
    }

    private static string JsonName(string property, JsonSerializerOptions options) =>
        options.PropertyNamingPolicy?.ConvertName(property) ?? property;

    private static bool TryParseWholeNumber(StringValues value, out int number) =>
        int.TryParse(value.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static ServiceProblemResult Bad(string code, string detail) =>
        new(StatusCodes.Status400BadRequest, code, detail);
}
