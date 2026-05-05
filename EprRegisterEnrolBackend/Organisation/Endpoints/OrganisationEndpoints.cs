using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using FluentValidation;
using FluentValidation.Results;

namespace EprRegisterEnrolBackend.Organisation.Endpoints;

public static class OrganisationEndpoints
{
    public static void UseOrganisationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("organisation", Create);

        app.MapGet("organisation", GetAll);

        app.MapGet("organisation/{orgId:int}", GetByOrgId);

        app.MapPut("organisation/{orgId:int}", Update);

        app.MapDelete("organisation/{orgId:int}", Delete);
    }

    private static async Task<IResult> Create(
        OrganisationModel organisation, IOrganisationPersistence organisationPersistence, IValidator<OrganisationModel> validator)
    {
        var validationResult = await validator.ValidateAsync(organisation);
        if (!validationResult.IsValid) return Results.BadRequest(validationResult.Errors);

        var created = await organisationPersistence.CreateAsync(organisation);
        if (!created)
            return Results.BadRequest(new List<ValidationFailure>
            {
                new("Organisation", "An organisation record with this orgId already exists")
            });

        return Results.Created($"/organisation/{organisation.OrgId}", organisation);
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

    private static async Task<IResult> Update(
        int orgId, OrganisationModel organisation, IOrganisationPersistence organisationPersistence,
        IValidator<OrganisationModel> validator)
    {
        organisation.OrgId = orgId;
        var validationResult = await validator.ValidateAsync(organisation);
        if (!validationResult.IsValid) return Results.BadRequest(validationResult.Errors);

        var updated = await organisationPersistence.UpdateAsync(organisation);
        return updated ? Results.Ok(organisation) : Results.NotFound();
    }

    private static async Task<IResult> Delete(
        int orgId, IOrganisationPersistence organisationPersistence)
    {
        var deleted = await organisationPersistence.DeleteAsync(orgId);
        return deleted ? Results.Ok() : Results.NotFound();
    }
}
