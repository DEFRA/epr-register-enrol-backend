using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-705 (decided 2026-11-16): with <c>DomainModel:ReadFromModel</c> on, ApplicationResponseReaderFilter serves a
/// write's response again from the model only when the body is an application, an overseas site or an interim site.
/// Every other body is served as the handler built it. This table names, for every non-GET route in the
/// accreditation-applications group, which of those it is, so a new route (or a changed one) fails here until someone
/// decides how it is served instead of quietly staying on legacy.
/// </summary>
public sealed partial class DualWriteEndpointTests
{
    public enum WriteResponse
    {
        /// <summary>The whole application, re-served from the model.</summary>
        Application,

        /// <summary>An overseas site, re-served from the model's copy of the application.</summary>
        Site,

        /// <summary>An interim site, re-served from the model's copy of the application.</summary>
        InterimSite,

        /// <summary>No body (204, or a bare 200): nothing to serve from either source.</summary>
        NoBody,

        /// <summary>Not built from the application (Submit's receipt, an upload's CDP details).</summary>
        NotFromTheApplication,

        /// <summary>
        /// Part of the application, but not a shape the filter re-serves: served from legacy, which dual-write keeps
        /// current, until phase 6 (RA-706) moves it to the model.
        /// </summary>
        LegacyUntilPhase6,
    }

    private static readonly Dictionary<string, WriteResponse> s_writeResponses = new()
    {
        ["POST {organisationId}/{registrationId}/{materialType}/seed"] = WriteResponse.Application,
        ["PATCH {organisationId}/{applicationId}/prns"] = WriteResponse.Application,
        ["PATCH {organisationId}/{applicationId}/tonnage"] = WriteResponse.Application,
        ["PATCH {organisationId}/{applicationId}/business-plan"] = WriteResponse.Application,
        ["PATCH {organisationId}/{applicationId}/sampling-plan"] = WriteResponse.Application,
        ["PATCH {organisationId}/{applicationId}/overseas-sites"] = WriteResponse.Application,
        ["POST {organisationId}/{applicationId}/overseas-sites"] = WriteResponse.Site,
        ["PATCH {organisationId}/{applicationId}/overseas-sites/{siteId}"] = WriteResponse.Site,
        ["POST {organisationId}/{applicationId}/overseas-sites/{siteId}/promote"] =
            WriteResponse.Site,
        ["POST {organisationId}/{applicationId}/overseas-sites/{siteId}/revert"] =
            WriteResponse.Site,
        ["PATCH {organisationId}/{applicationId}/overseas-sites/{siteId}/recycling-operations"] =
            WriteResponse.Site,
        ["POST {organisationId}/{applicationId}/overseas-sites/{siteId}/interim-site"] =
            WriteResponse.InterimSite,
        ["POST {organisationId}/{applicationId}/overseas-sites/{siteId}/interim-sites"] =
            WriteResponse.InterimSite,
        [
            "PATCH {organisationId}/{applicationId}/overseas-sites/{siteId}/interim-sites/{interimSiteId}"
        ] = WriteResponse.InterimSite,
        [
            "DELETE {organisationId}/{applicationId}/overseas-sites/{siteId}/interim-sites/{interimSiteId}"
        ] = WriteResponse.NoBody,
        [
            "POST {organisationId}/{applicationId}/overseas-sites/{siteId}/interim-sites/{interimSiteId}/restore"
        ] = WriteResponse.InterimSite,
        // The site's BES evidence object, not the site.
        ["POST {organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files"] =
            WriteResponse.LegacyUntilPhase6,
        ["PATCH {organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence"] =
            WriteResponse.Application,
        [
            "DELETE {organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files/{fileId}"
        ] = WriteResponse.NoBody,
        [
            "PATCH {organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files/{fileId}"
        ] = WriteResponse.Application,
        ["PATCH {organisationId}/{applicationId}/bes-evidence"] = WriteResponse.Application,
        ["POST {organisationId}/{applicationId}/submit"] = WriteResponse.NotFromTheApplication,
        ["POST {organisationId}/{applicationId}/resubmit"] = WriteResponse.Application,
        ["POST {organisationId}/{applicationId}/withdraw"] = WriteResponse.Application,
        ["POST case-management/{workItemId}/query"] = WriteResponse.Application,
        ["POST case-management/{workItemId}/status"] = WriteResponse.Application,
        ["POST {organisationId}/{applicationId}/registration-number"] = WriteResponse.Application,
        ["POST {organisationId}/{applicationId}/accreditation-number"] = WriteResponse.Application,
        // The uploaded file's record, not the application.
        ["POST {organisationId}/{applicationId}/files"] = WriteResponse.LegacyUntilPhase6,
        ["DELETE {organisationId}/{applicationId}/files/{fileId}"] = WriteResponse.NoBody,
        ["POST {organisationId}/{applicationId}/files/initiate"] =
            WriteResponse.NotFromTheApplication,
        ["POST {organisationId}/{applicationId}/files/bes-evidence/initiate"] =
            WriteResponse.NotFromTheApplication,
        ["POST files/upload-completed"] = WriteResponse.NoBody,
    };

    private const string GroupPrefix = "api/v1/accreditation-applications/";

    private List<string> NonGetRoutesInTheGroup() =>
        [
            .. _factory
                .Services.GetRequiredService<EndpointDataSource>()
                .Endpoints.OfType<RouteEndpoint>()
                .Select(endpoint =>
                    (
                        Pattern: endpoint.RoutePattern.RawText?.TrimStart('/') ?? "",
                        Methods: endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                            ?? []
                    )
                )
                .Where(route => route.Pattern.StartsWith(GroupPrefix, StringComparison.Ordinal))
                .SelectMany(route =>
                    route
                        .Methods.Where(method => method != "GET")
                        .Select(method => $"{method} {route.Pattern[GroupPrefix.Length..]}")
                )
                .Distinct()
                .Order(StringComparer.Ordinal),
        ];

    [Fact]
    public void ReadFromModel_On_EveryWriteRouteHasADecidedResponse()
    {
        Restart(s_readFromModelOn);

        var routes = NonGetRoutesInTheGroup();

        Assert.NotEmpty(routes);
        var undecided = routes.Where(route => !s_writeResponses.ContainsKey(route)).ToList();
        Assert.True(
            undecided.Count == 0,
            "Write routes with no decided response under ReadFromModel (add each to s_writeResponses): "
                + string.Join(", ", undecided)
        );
        var gone = s_writeResponses.Keys.Except(routes).ToList();
        Assert.True(
            gone.Count == 0,
            "s_writeResponses names routes that no longer exist: " + string.Join(", ", gone)
        );
    }

    // The two bodies knowingly left on legacy until phase 6, named so the PR and the design doc can point at them.
    [Fact]
    public void ReadFromModel_On_OnlyTheBesEvidenceAndFileAddsStayOnLegacyUntilPhase6()
    {
        var legacy = s_writeResponses
            .Where(pair => pair.Value == WriteResponse.LegacyUntilPhase6)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "POST {organisationId}/{applicationId}/files",
                "POST {organisationId}/{applicationId}/overseas-sites/{siteId}/bes-evidence/files",
            ],
            legacy
        );
    }
}
