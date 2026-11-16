using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Auth;

/// <summary>
/// A request limit for each service client, so one client in a loop cannot use up the service for
/// the others. Each client has its own count, kept by client id, so a busy client is limited and a
/// quiet one is not. Counted in memory in each instance, which is enough to stop a runaway client;
/// with several instances a client can make up to the limit on each.
/// </summary>
public sealed class ServiceClientRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string>? _limiter;

    public ServiceClientRateLimiter(IOptions<ServiceClientsConfig> options)
    {
        var permits = options.Value.RateLimitPermitsPerMinute;
        if (permits <= 0)
            return;

        _limiter = PartitionedRateLimiter.Create<string, string>(clientId =>
            RateLimitPartition.GetFixedWindowLimiter(
                clientId,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permits,
                    Window = Window,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }
            )
        );
    }

    public static TimeSpan Window { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Counts one request for the client. False, with how long to wait, when it is over.</summary>
    public bool TryAcquire(string clientId, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (_limiter is null)
            return true;

        using var lease = _limiter.AttemptAcquire(clientId);
        if (lease.IsAcquired)
            return true;

        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? wait : Window;
        return false;
    }

    public void Dispose() => _limiter?.Dispose();
}

/// <summary>
/// Answers 429 (<c>rate_limited</c>, with <c>Retry-After</c>) to a client that has made too many
/// requests. It runs after authentication, so the count is for the client that signed the request
/// and cannot be spent by anyone naming that client without its secret.
/// </summary>
public sealed class ServiceClientRateLimitFilter(ServiceClientRateLimiter limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next
    )
    {
        var http = context.HttpContext;
        var clientId = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "(unknown)";
        if (limiter.TryAcquire(clientId, out var retryAfter))
            return await next(context);

        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return new ServiceProblemResult(
            StatusCodes.Status429TooManyRequests,
            "rate_limited",
            $"Too many requests. Wait {seconds} seconds, then try again."
        );
    }
}
