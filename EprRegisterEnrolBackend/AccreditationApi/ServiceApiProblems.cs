using System.Globalization;
using EprRegisterEnrolBackend.AdminApi;
using EprRegisterEnrolBackend.Auth;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApi;

/// <summary>
/// RA-702: one error format across the service API. The routes already answer 400, 401, 403, 404
/// and 429 as problem details with a stable <c>code</c> and the <c>traceId</c>. ASP.NET Core itself
/// answers a path that matches no route (404) and a method a route does not take (405) with an empty
/// body, so under <c>/v1</c> those are written the same way here. RA-740: the same for the admin API
/// under <c>/admin</c>.
/// </summary>
public static class ServiceApiProblems
{
    private static readonly PathString[] ApiPrefixes = ["/v1", AdminApiEndpoints.PathPrefix];

    public static IApplicationBuilder UseServiceApiProblems(this IApplicationBuilder app) =>
        app.Use(
            async (context, next) =>
            {
                await next();

                if (!ApiPrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
                    return;

                var response = context.Response;
                if (
                    response.HasStarted
                    || response.ContentLength > 0
                    || !string.IsNullOrEmpty(response.ContentType)
                )
                    return;

                switch (response.StatusCode)
                {
                    case StatusCodes.Status404NotFound:
                        await ServiceClientAuthenticationHandler.WriteProblemAsync(
                            context,
                            StatusCodes.Status404NotFound,
                            "not_found",
                            "There is no such route."
                        );
                        break;
                    case StatusCodes.Status405MethodNotAllowed:
                        await ServiceClientAuthenticationHandler.WriteProblemAsync(
                            context,
                            StatusCodes.Status405MethodNotAllowed,
                            "method_not_allowed",
                            $"{context.Request.Method} is not allowed on this route. The API is read-only: use GET."
                        );
                        break;
                }
            }
        );
}

/// <summary>
/// Answers 503 (<c>query_timeout</c>, with <c>Retry-After</c>) when a database query for a service
/// client ran past its limit (<see cref="ServiceClientsConfig.QueryTimeoutSeconds"/>) and the
/// database stopped it, instead of a bare 500.
/// </summary>
public sealed class ServiceClientQueryTimeoutFilter(ILoggerFactory loggerFactory) : IEndpointFilter
{
    private readonly ILogger _logger = loggerFactory.CreateLogger(
        "EprRegisterEnrolBackend.AccreditationApi"
    );

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        try
        {
            return await next(context);
        }
        catch (MongoExecutionTimeoutException ex)
        {
            var http = context.HttpContext;
            _logger.LogWarning(
                ex,
                "Service client query timed out: method={Method} path={Path}",
                http.Request.Method,
                http.Request.Path.Value
            );
            http.Response.Headers.RetryAfter = 5.ToString(CultureInfo.InvariantCulture);
            return new ServiceProblemResult(
                StatusCodes.Status503ServiceUnavailable,
                "query_timeout",
                "The request took too long to answer. Try again, with a narrower search if you can."
            );
        }
    }
}
