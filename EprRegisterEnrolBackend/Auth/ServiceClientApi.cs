using System.Diagnostics;
using System.Security.Claims;

namespace EprRegisterEnrolBackend.Auth;

/// <summary>
/// The service API's error format, as a result: problem details (RFC 9457) with a stable
/// <c>code</c> and the <c>traceId</c>, the same shape the authentication failures use.
/// </summary>
public sealed class ServiceProblemResult(int status, string code, string detail) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) =>
        ServiceClientAuthenticationHandler.WriteProblemAsync(httpContext, status, code, detail);
}

/// <summary>
/// Logs one line for every authenticated service client request, naming the client, so a call
/// can always be traced to who made it. Logged under its own category so it can be switched on
/// separately from the rest of the service (the default level is Warning). It only ever reads
/// the client id and name claims and the route values: the secret, the signature and the nonce
/// never reach it.
/// </summary>
public sealed class ServiceClientAccessLogFilter(ILoggerFactory loggerFactory) : IEndpointFilter
{
    public const string LogCategory = "EprRegisterEnrolBackend.ServiceClientAccess";

    private readonly ILogger _logger = loggerFactory.CreateLogger(LogCategory);

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var http = context.HttpContext;
        var clientId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(unknown)";
        var clientName =
            http.User.FindFirstValue(ServiceClientAuthenticationHandler.ClientNameClaimType)
            ?? "(unknown)";
        var route = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? http.Request.Path;
        var values = http.Request.RouteValues;
        var organisationNumber = values["organisationNumber"]?.ToString();
        var registrationNumber = values["registrationNumber"]?.ToString();
        var year = values["year"]?.ToString();
        var started = Stopwatch.StartNew();

        using var scope = _logger.BeginScope(
            new Dictionary<string, object> { ["ClientId"] = clientId, ["ClientName"] = clientName }
        );

        // The status is only known once the response has been written, so log then.
        http.Response.OnCompleted(() =>
        {
            _logger.LogInformation(
                "Service client request: clientId={ClientId} clientName={ClientName} method={Method} route={Route} organisationNumber={OrganisationNumber} registrationNumber={RegistrationNumber} year={Year} status={Status} durationMs={DurationMs}",
                clientId,
                clientName,
                http.Request.Method,
                route,
                organisationNumber,
                registrationNumber,
                year,
                http.Response.StatusCode,
                started.ElapsedMilliseconds
            );
            return Task.CompletedTask;
        });

        return await next(context);
    }
}
