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

    /// <summary>
    /// RA-740: <see cref="Read"/>, and the client must be the admin service client: the one whose
    /// configured <see cref="ServiceClientDefinition.Name"/> is <see cref="AdminClientName"/>, ignoring case. Guards
    /// the <c>/admin</c> API. Any other known client, such as ReEx, is answered 403.
    /// </summary>
    public const string Admin = "ServiceClientAdmin";

    /// <summary>The configured name of the admin service client (<c>ServiceClients__Clients__N__Name</c>).</summary>
    public const string AdminClientName = "admin";

    public static void AddServiceClientPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(Read, policy => RequireReadingClient(policy));
        options.AddPolicy(
            Admin,
            policy =>
                // Ignoring case: an "Admin" entry finds the same secret, so it is the same client.
                RequireReadingClient(policy)
                    .RequireAssertion(context =>
                        context.User.HasClaim(claim =>
                            claim.Type == ServiceClientAuthenticationHandler.ClientNameClaimType
                            && string.Equals(claim.Value, AdminClientName, StringComparison.OrdinalIgnoreCase)
                        )
                    )
        );
    }

    private static AuthorizationPolicyBuilder RequireReadingClient(AuthorizationPolicyBuilder policy) =>
        policy
            .AddAuthenticationSchemes(ServiceClientAuthenticationHandler.SchemeName)
            .RequireAuthenticatedUser()
            .RequireClaim(
                ServiceClientAuthenticationHandler.ScopeClaimType,
                ServiceClientAuthenticationHandler.ReadScope
            )
            .AddRequirements(new ReadOnlyMethodRequirement());
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