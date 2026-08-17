using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using FluentValidation;

namespace EprRegisterEnrolBackend.Organisation.Endpoints;

// GetAll/GetByOrgId/Upsert are the only routes with a live caller: the frontend's
// persistentStubApiClient (active whenever api.stubEnabled=true, e.g. local
// docker-compose/e2e) write-throughs stub organisation data here so it survives
// across requests. Create/Update/Delete were removed alongside FileUploadEndpoints
// as genuinely dead code — restored these three after a PR review caught that the
// stub write-through path depends on them (see PR #107 review).
//
// Only registered in Development (see UseOrganisationEndpoints call in Program.cs) —
// persistentStubApiClient's write-through only ever targets a Development-environment
// backend (local docker-compose, fe-tests CI), so there's no legitimate caller in any
// deployed environment. Gating it out entirely is safer than authenticating it: it
// simply doesn't exist as attack surface outside that context, rather than existing
// but locked.
public static class OrganisationEndpoints
{
    public static void UseOrganisationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("organisation", GetAll);

        app.MapGet("organisation/{orgId:int}", GetByOrgId);

        app.MapPut("organisation/{orgId:int}/upsert", Upsert);
    }

    private static async Task<IResult> GetAll(IOrganisationPersistence organisationPersistence, string? searchTerm)
    {
        if (searchTerm is not null && !string.IsNullOrWhiteSpace(searchTerm))
        {
            var matched = await organisationPersistence.SearchByValueAsync(searchTerm);
            return Results.Ok(matched);
        }

        var matches = await organisationPersistence.GetAllAsync();
        return Results.Ok(matches);
    }

    private static async Task<IResult> GetByOrgId(
        int orgId, IOrganisationPersistence organisationPersistence)
    {
        var organisation = await organisationPersistence.GetByOrgIdAsync(orgId);
        return organisation is not null ? Results.Ok(organisation) : Results.NotFound();
    }

    private static async Task<IResult> Upsert(
        int orgId, OrganisationModel organisation, IOrganisationPersistence organisationPersistence,
        IValidator<OrganisationModel> validator)
    {
        organisation.OrgId = orgId;
        var validationResult = await validator.ValidateAsync(organisation);
        if (!validationResult.IsValid) return Results.BadRequest(validationResult.Errors);

        var upserted = await organisationPersistence.UpsertAsync(organisation);
        return upserted ? Results.Ok(organisation) : Results.Problem("Failed to upsert organisation.");
    }
}
