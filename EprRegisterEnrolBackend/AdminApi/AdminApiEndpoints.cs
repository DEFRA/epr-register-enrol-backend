using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

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
    /// <summary>
    /// Where the admin API is mapped. Its error format (<see cref="ServiceApiProblems"/>) and its
    /// log redaction (<c>PiiRedactionEnricher</c>) key on the same prefix.
    /// </summary>
    public const string PathPrefix = "/admin";

    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;
    private const int MaxSearchLength = 100;
    private const int MaxReferenceLength = 100;
    private const int MinYear = 2000;

    // RA-751: one Admin UI results page of work items.
    private const int MaxWorkItemIds = 50;

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
        var group = app.MapGroup(PathPrefix)
            .RequireAuthorization(ServiceClientPolicies.Admin)
            .AddEndpointFilter<ServiceClientAccessLogFilter>()
            .AddEndpointFilter<ServiceClientRateLimitFilter>()
            .AddEndpointFilter<ServiceClientQueryTimeoutFilter>()
            .WithTags("Admin");

        group
            .MapGet("/accreditation-applications", SearchLegacyAccreditationApplications)
            .WithName("SearchLegacyAccreditationApplications");

        group
            .MapGet("/accreditation-applications/{reference}", GetLegacyAccreditationApplication)
            .WithName("GetLegacyAccreditationApplication");

        group
            .MapGet("/accreditation-application-statuses", GetLegacyAccreditationApplicationStatuses)
            .WithName("GetLegacyAccreditationApplicationStatuses");

        group
            .MapGet("/case-management-status-mapping", GetCaseManagementStatusMapping)
            .WithName("GetCaseManagementStatusMapping");
    }

    /// <summary>
    /// RA-751: the status of the Legacy application linked to each of a page of Case Management
    /// work items, in one query, so the Admin UI can set each work item beside the status its
    /// application holds. A work item with no linked application is listed as not found.
    /// </summary>
    private static async Task<IResult> GetLegacyAccreditationApplicationStatuses(
        HttpContext http,
        ILegacyApplicationRead applications,
        IOptions<ServiceClientsConfig> config,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken
    )
    {
        if (ParseWorkItemIds(http.Request.Query, out var workItemIds) is { } badIds)
            return badIds;

        IReadOnlyList<LegacyApplicationStatus> linked;
        try
        {
            linked = await applications.GetLegacyApplicationStatusesAsync(
                workItemIds,
                config.Value.QueryTimeout,
                cancellationToken
            );
        }
        catch (LegacyApplicationStatusUnreadableException ex)
        {
            loggerFactory
                .CreateLogger(typeof(AdminApiEndpoints).FullName!)
                .LogError(
                    ex,
                    "Admin Legacy status lookup: application {ApplicationId} linked to work item {WorkItemId} can't be read: {Reason}",
                    ex.ApplicationId.ToString(),
                    ex.WorkItemId,
                    ex.Reason
                );
            return new ServiceProblemResult(
                StatusCodes.Status500InternalServerError,
                "invalid_stored_application",
                "A Legacy application linked to one of the work items has no status the service can read. Quote the traceId to the delivery team."
            );
        }

        var byWorkItem = linked.ToDictionary(status => status.WorkItemId);
        return Results.Ok(
            new LegacyApplicationStatusesDto(
                workItemIds
                    .Select(id =>
                        byWorkItem.TryGetValue(id, out var status)
                            ? new LegacyApplicationStatusDto(
                                id,
                                true,
                                status.ApplicationId.ToString(),
                                status.ApplicationStatus
                            )
                            : new LegacyApplicationStatusDto(id, false, null, null)
                    )
                    .ToList()
            )
        );
    }

    /// <summary>
    /// RA-751: how each Case Management push lands on the application's status, from the table
    /// the push receivers apply (<see cref="CaseManagementStatusMapping"/>).
    /// </summary>
    private static IResult GetCaseManagementStatusMapping() =>
        Results.Ok(
            new CaseManagementStatusMappingDto(
                CaseManagementStatusMapping
                    .StatusPush.Select(push => new StatusPushMappingDto(push.Key, push.Value))
                    .ToList(),
                new QueryPushMappingDto(CaseManagementStatusMapping.QueryPushStatus)
            )
        );

    /// <summary>
    /// RA-750: one Legacy application, as stored in Mongo (relaxed extended JSON), found by its
    /// application reference, or by its <c>_id</c> when it has no reference yet.
    /// </summary>
    private static async Task<IResult> GetLegacyAccreditationApplication(
        string reference,
        ILegacyApplicationRead applications,
        IOptions<ServiceClientsConfig> config,
        CancellationToken cancellationToken
    )
    {
        var trimmed = reference.Trim();
        if (trimmed.Length == 0 || trimmed.Length > MaxReferenceLength || trimmed.Any(char.IsControl))
        {
            return Bad(
                "invalid_reference",
                $"reference must be 1 to {MaxReferenceLength} characters, with no control characters."
            );
        }

        var stored = await applications.GetLegacyApplicationAsync(
            trimmed,
            config.Value.QueryTimeout,
            cancellationToken
        );
        if (stored is null)
        {
            return new ServiceProblemResult(
                StatusCodes.Status404NotFound,
                "application_not_found",
                "No accreditation application has that reference or id."
            );
        }

        return Results.Text(
            stored.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.RelaxedExtendedJson }),
            "application/json",
            System.Text.Encoding.UTF8
        );
    }

    private static async Task<IResult> SearchLegacyAccreditationApplications(
        HttpContext http,
        ILegacyApplicationSearch applications,
        IOptions<ServiceClientsConfig> config,
        IOptions<HttpJsonOptions> json,
        CancellationToken cancellationToken
    )
    {
        var query = http.Request.Query;
        if (ParseText(query, out var q) is { } badText)
            return badText;
        if (ParseSearchFields(query, out var fields) is { } badFields)
            return badFields;
        if (ParseStatuses(query, out var statuses) is { } badStatus)
            return badStatus;
        if (ParseYear(query, out var year) is { } badYear)
            return badYear;
        if (ParsePaging(query, out var pageNumber, out var limit) is { } badPaging)
            return badPaging;

        var (items, total) = await applications.SearchLegacyApplicationsAsync(
            new LegacyApplicationSearchCriteria(q, fields, statuses, year),
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

    // Each Parse* returns the problem to send back, or null with the value parsed.

    private static ServiceProblemResult? ParseText(IQueryCollection query, out string? q)
    {
        q = null;
        var text = query["q"];
        if (text.Count > 1)
            return Bad("invalid_q", "q may be given once.");
        var trimmed = text.ToString().Trim();
        if (trimmed.Length > MaxSearchLength)
            return Bad("invalid_q", $"q must be at most {MaxSearchLength} characters.");
        // A Mongo regex can't hold a NUL, and no number or name holds a control character.
        if (trimmed.Any(char.IsControl))
            return Bad("invalid_q", "q must not contain control characters.");
        q = trimmed.Length == 0 ? null : trimmed;
        return null;
    }

    private static ServiceProblemResult? ParseWorkItemIds(IQueryCollection query, out List<Guid> workItemIds)
    {
        workItemIds = [];
        var values = query["workItemId"];
        if (values.Count == 0)
        {
            return Bad(
                "invalid_work_item_id",
                $"workItemId is required: give 1 to {MaxWorkItemIds} work item ids, each as its own workItemId."
            );
        }

        if (values.Count > MaxWorkItemIds)
            return Bad("invalid_work_item_id", $"workItemId may be given at most {MaxWorkItemIds} times.");

        foreach (var value in values)
        {
            // 36 characters: Guid parsing would otherwise trim padding the caller sent.
            if (value is not { Length: 36 } || !Guid.TryParseExact(value, "D", out var id))
            {
                return Bad(
                    "invalid_work_item_id",
                    "workItemId must each be a work item id, a GUID such as 3f2b8c1e-4d5a-4b6c-9e7f-0a1b2c3d4e5f."
                );
            }

            if (workItemIds.Contains(id))
                return Bad("invalid_work_item_id", $"workItemId {id} is given more than once.");
            workItemIds.Add(id);
        }

        return null;
    }

    private static ServiceProblemResult? ParseSearchFields(
        IQueryCollection query,
        out List<LegacyApplicationSearchField> fields
    )
    {
        fields = [];
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

        return null;
    }

    private static ServiceProblemResult? ParseStatuses(
        IQueryCollection query,
        out List<ApplicationStatus> statuses
    )
    {
        statuses = [];
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

        return null;
    }

    private static ServiceProblemResult? ParseYear(IQueryCollection query, out int? year)
    {
        year = null;
        if (!query.TryGetValue("year", out var value))
            return null;
        if (!TryParseWholeNumber(value, out var parsed) || parsed < MinYear)
            return Bad("invalid_year", $"year must be a whole number from {MinYear}, for example 2027.");
        year = parsed;
        return null;
    }

    private static ServiceProblemResult? ParsePaging(
        IQueryCollection query,
        out int pageNumber,
        out int limit
    )
    {
        pageNumber = 1;
        limit = DefaultLimit;
        if (
            !StringValues.IsNullOrEmpty(query["pageNumber"])
            && (!TryParseWholeNumber(query["pageNumber"], out pageNumber) || pageNumber < 1)
        )
        {
            return Bad("invalid_page_number", "pageNumber must be a whole number from 1.");
        }

        if (
            !StringValues.IsNullOrEmpty(query["limit"])
            && (!TryParseWholeNumber(query["limit"], out limit) || limit < 1 || limit > MaxLimit)
        )
        {
            return Bad("invalid_limit", $"limit must be a whole number from 1 to {MaxLimit}.");
        }

        return null;
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
