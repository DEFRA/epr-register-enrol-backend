using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

/// <summary>
/// With DomainModel:ReadFromModel on, the PATCH/POST responses of the accreditation-application routes,
/// including site and interim-site bodies, must come from the domain model. Each write handler returns a result
/// built from the legacy document before the sync has copied the change to the model, so the response is
/// served again via the reader.
/// </summary>
public sealed class ApplicationResponseReaderFilter(IApplicationReader reader) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var result = await next(context);

        if (HttpMethods.IsGet(context.HttpContext.Request.Method))
        {
            return result;
        }

        var ct = context.HttpContext.RequestAborted;

        // Each step answers null when the result is not its kind, so the first match wins and anything else passes
        // through unchanged.
        return await RewrapApplicationAsync(result, ct)
            ?? await RewrapOverseasSiteAsync(result, context, ct)
            ?? await RewrapInterimSiteAsync(result, context, ct)
            ?? result;
    }

    private Task<object?> RewrapApplicationAsync(object? result, CancellationToken ct)
    {
        return Rewrap<AccreditationApplicationModel>(
            result,
            v => reader.ServeFromSourceAsync(v, ct)
        );
    }

    private Task<object?> RewrapOverseasSiteAsync(
        object? result,
        EndpointFilterInvocationContext context,
        CancellationToken ct
    )
    {
        var orgId = RouteValue(context, "organisationId");
        var appId = RouteValue(context, "applicationId");

        return Rewrap<OverseasSiteModel>(
            result,
            async v =>
            {
                var app = await reader.GetByIdAsync(orgId, appId, ct);
                return app?.OverseasSites?.Sites?.FirstOrDefault(s => s.SiteId == v.SiteId);
            }
        );
    }

    private Task<object?> RewrapInterimSiteAsync(
        object? result,
        EndpointFilterInvocationContext context,
        CancellationToken ct
    )
    {
        var orgId = RouteValue(context, "organisationId");
        var appId = RouteValue(context, "applicationId");
        var routeSiteIdStr = context.HttpContext.Request.RouteValues["siteId"]?.ToString();

        return Rewrap<InterimSiteModel>(
            result,
            async v =>
            {
                var app = await reader.GetByIdAsync(orgId, appId, ct);
                return app is null ? null : FindInterimSite(app, routeSiteIdStr, v);
            }
        );
    }

    // Looks under the site the route names, and only there: interim site ids repeat across sites, so a site that
    // doesn't hold it answers null (404, as for a read) rather than another site's interim with the same id. Only a
    // route that names no site, or one that doesn't parse, searches the whole application.
    private static InterimSiteModel? FindInterimSite(
        AccreditationApplicationModel app,
        string? routeSiteIdStr,
        InterimSiteModel written
    )
    {
        if (
            int.TryParse(
                routeSiteIdStr,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var routeSiteId
            )
        )
        {
            var site = app.OverseasSites?.Sites?.FirstOrDefault(s => s.SiteId == routeSiteId);
            if (site is not null)
            {
                return site.InterimSites?.FirstOrDefault(i => i.SiteId == written.SiteId);
            }
        }

        return app
            .OverseasSites?.Sites?.SelectMany(s => s.InterimSites ?? [])
            .FirstOrDefault(i => i.SiteId == written.SiteId);
    }

    private static string RouteValue(EndpointFilterInvocationContext context, string name)
    {
        return context.HttpContext.Request.RouteValues[name]?.ToString() ?? string.Empty;
    }

    private static async Task<object?> Rewrap<T>(object? result, Func<T, Task<T?>> fromSource)
        where T : class
    {
        if (result is Ok<T> { Value: { } ok })
        {
            var served = await fromSource(ok);

            // Write responses are strict like reads: the source not holding the entity answers 404.
            return served is null ? TypedResults.NotFound() : TypedResults.Ok(served);
        }

        if (result is Created<T> { Value: { } created, Location: var location })
        {
            var served = await fromSource(created);

            return served is null
                ? TypedResults.NotFound()
                : TypedResults.Created(location, served);
        }

        return null;
    }
}
