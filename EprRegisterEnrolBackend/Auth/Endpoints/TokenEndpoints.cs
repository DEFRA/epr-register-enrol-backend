using EprRegisterEnrolBackend.Auth.Models;
using EprRegisterEnrolBackend.Auth.Services;

namespace EprRegisterEnrolBackend.Auth.Endpoints;

public static class TokenEndpoints
{
    public static void UseTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/token", RequestToken)
            .WithName("RequestToken")
            .AllowAnonymous();
    }

    private static async Task<IResult> RequestToken(TokenRequest request, ITokenService tokenService)
    {
        // Validate grant_type
        if (string.IsNullOrWhiteSpace(request.grant_type) || request.grant_type != "client_credentials")
        {
            return Results.BadRequest(new TokenErrorResponse("unsupported_grant_type", "Only client_credentials grant type is supported"));
        }

        // Validate client credentials
        if (string.IsNullOrWhiteSpace(request.client_id) || string.IsNullOrWhiteSpace(request.client_secret))
        {
            return Results.BadRequest(new TokenErrorResponse("invalid_request", "client_id and client_secret are required"));
        }

        // Generate token
        var tokenResponse = tokenService.GenerateToken(request.client_id, request.client_secret);
        if (tokenResponse is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(tokenResponse);
    }
}

