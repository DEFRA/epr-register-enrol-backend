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

        if (result is Ok<AccreditationApplicationModel> or Created<AccreditationApplicationModel>)
        {
            var rewrapped = await Rewrap<AccreditationApplicationModel>(
                result,
                v => reader.ServeFromSourceAsync(v, ct)
            );
            if (rewrapped is not null)
            {
                return rewrapped;
            }
        }

        if (result is Ok<OverseasSiteModel> or Created<OverseasSiteModel>)
        {
            var orgId = RouteValue(context, "organisationId");
            var appId = RouteValue(context, "applicationId");

            var rewrapped = await Rewrap<OverseasSiteModel>(
                result,
                async v =>
                {
                    var app = await reader.GetByIdAsync(orgId, appId, ct);
                    return app?.OverseasSites?.Sites?.FirstOrDefault(s => s.SiteId == v.SiteId);
                }
            );
            if (rewrapped is not null)
            {
                return rewrapped;
            }
        }

        if (result is Ok<InterimSiteModel> or Created<InterimSiteModel>)
        {
            var orgId = RouteValue(context, "organisationId");
            var appId = RouteValue(context, "applicationId");
            var routeSiteIdStr = context.HttpContext.Request.RouteValues["siteId"]?.ToString();

            var rewrapped = await Rewrap<InterimSiteModel>(
                result,
                async v =>
                {
                    var app = await reader.GetByIdAsync(orgId, appId, ct);
                    if (app is null)
                    {
                        return null;
                    }

                    if (
                        int.TryParse(
                            routeSiteIdStr,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var routeSiteId
                        )
                    )
                    {
                        var site = app.OverseasSites?.Sites?.FirstOrDefault(s =>
                            s.SiteId == routeSiteId
                        );
                        if (site is not null)
                        {
                            return site.InterimSites?.FirstOrDefault(i => i.SiteId == v.SiteId);
                        }
                    }

                    return app
                        .OverseasSites?.Sites?.SelectMany(s => s.InterimSites ?? [])
                        .FirstOrDefault(i => i.SiteId == v.SiteId);
                }
            );
            if (rewrapped is not null)
            {
                return rewrapped;
            }
        }

        return result;
    }

    private static string RouteValue(EndpointFilterInvocationContext context, string name)
    {
        return context.HttpContext.Request.RouteValues[name]?.ToString() ?? string.Empty;
    }

    private static async Task<object?> Rewrap<T>(object result, Func<T, Task<T?>> fromSource)
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
