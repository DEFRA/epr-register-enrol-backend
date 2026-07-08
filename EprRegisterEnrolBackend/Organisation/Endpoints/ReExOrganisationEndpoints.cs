using EprRegisterEnrolBackend.AccreditationApplication.Adapters;

namespace EprRegisterEnrolBackend.Organisation.Endpoints;

/// <summary>
/// ReEx-backed organisation endpoints. Distinct from the Mongo-backed OrganisationEndpoints:
/// these read live from the ReEx API rather than the local organisation_epr collection.
/// </summary>
public static class ReExOrganisationEndpoints
{
    public static void UseReExOrganisationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/organisations");

        // Returns the Defra organisation an operator must be related to in order to
        // access this ReEx organisation. Consumed by the frontend authorisation guard.
        group.MapGet("{organisationId}/defra-link", GetDefraLink);
    }

    private static async Task<IResult> GetDefraLink(
        string organisationId,
        IReExApiAdapter reExAdapter,
        CancellationToken cancellationToken
    )
    {
        var result = await reExAdapter.GetLinkedDefraOrganisationAsync(
            organisationId,
            cancellationToken
        );

        if (result.IsSuccess)
            return Results.Ok(result.Value);

        if (result.IsNotFound)
            return Results.NotFound();

        return Results.Problem(
            result.Error?.Message ?? "Failed to resolve linked Defra organisation",
            statusCode: result.StatusCode ?? 502
        );
    }
}
