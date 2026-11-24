using EprRegisterEnrolBackend.Auth;

namespace EprRegisterEnrolBackend.AccreditationApplication.Endpoints;

internal static class CaseManagementEndpointExtensions
{
    /// <summary>
    /// Restricts a route to the Case Management caller (management-be), on the CaseManagement scheme.
    /// </summary>
    public static TBuilder RequireCaseManagementCaller<TBuilder>(this TBuilder endpoint)
        where TBuilder : IEndpointConventionBuilder =>
        endpoint.RequireAuthorization(policy =>
            policy
                .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
        );
}
