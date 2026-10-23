using Microsoft.AspNetCore.Authorization;

namespace EprRegisterEnrolBackend.Auth;

public static class ServiceClientPolicies
{
    /// <summary>
    /// A service client with the <c>read</c> scope, making a read-only request (GET or HEAD).
    /// Every service client has this scope and no other: there is no per-client permission model
    /// yet, and no way for a service client to record a change.
    /// </summary>
    public const string Read = "ServiceClientRead";

    public static void AddServiceClientPolicies(this AuthorizationOptions options) =>
        options.AddPolicy(
            Read,
            policy =>
                policy
                    .AddAuthenticationSchemes(ServiceClientAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
                    .RequireClaim(
                        ServiceClientAuthenticationHandler.ScopeClaimType,
                        ServiceClientAuthenticationHandler.ReadScope
                    )
                    .AddRequirements(new ReadOnlyMethodRequirement())
        );
}

/// <summary>
/// Refuses anything but GET and HEAD. The routes that use <see cref="ServiceClientPolicies.Read"/>
/// are GET-only already; this keeps "read only" true if a write route is ever given the policy by
/// mistake.
/// </summary>
public sealed class ReadOnlyMethodRequirement : IAuthorizationRequirement;

public sealed class ReadOnlyMethodHandler : AuthorizationHandler<ReadOnlyMethodRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ReadOnlyMethodRequirement requirement
    )
    {
        if (
            context.Resource is HttpContext http
            && (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method))
        )
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
