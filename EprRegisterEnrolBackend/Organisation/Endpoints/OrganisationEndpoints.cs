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

        app.MapGet("organisation/{name}", GetByName);

        app.MapPut("organisation/{name}", Update);

        app.MapDelete("organisation/{name}", Delete);
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
                new("Organisation", "An organisation record with this company name or registration number already exists")
            });

        return Results.Created($"/organisation/{organisation.CompanyName}", organisation);
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

    private static async Task<IResult> GetByName(
        string name, IOrganisationPersistence organisationPersistence)
    {
        var organisation = await organisationPersistence.GetByOrganisationName(name);
        return organisation is not null ? Results.Ok(organisation) : Results.NotFound();
    }

    private static async Task<IResult> Update(
        string name, OrganisationModel organisation, IOrganisationPersistence organisationPersistence,
        IValidator<OrganisationModel> validator)
    {
        organisation.CompanyName = name;
        var validationResult = await validator.ValidateAsync(organisation);
        if (!validationResult.IsValid) return Results.BadRequest(validationResult.Errors);

        var updated = await organisationPersistence.UpdateAsync(organisation);
        return updated ? Results.Ok(organisation) : Results.NotFound();
    }

    private static async Task<IResult> Delete(
        string name, IOrganisationPersistence organisationPersistence)
    {
        var deleted = await organisationPersistence.DeleteAsync(name);
        return deleted ? Results.Ok() : Results.NotFound();
    }
}
