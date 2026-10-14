using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public static class FixedAccreditationEndpoints
{
    public static void UseFixedAccreditationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/accreditation-changes")
            .RequireAuthorization(policy => policy
                .AddAuthenticationSchemes(FrontendAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser());
        group.MapGet("target", ReadConfiguredTarget);
        group.MapGet("{organisationId}/{registrationId}/{accreditationId}/{materialType}/{year:int}", ReadTarget);
    }

    private static Task<IResult> ReadConfiguredTarget(
        IOptions<AccreditationChangeSpikeOptions> options,
        IHostEnvironment environment,
        IReExApiAdapter reExAdapter) => ReadBaseline(options.Value, environment, reExAdapter);

    private static Task<IResult> ReadTarget(
        string organisationId,
        string registrationId,
        string accreditationId,
        MaterialType materialType,
        int year,
        IOptions<AccreditationChangeSpikeOptions> options,
        IHostEnvironment environment,
        IReExApiAdapter reExAdapter)
    {
        var target = options.Value;
        // The development adapter synthesizes accreditation responses for arbitrary inputs.
        // Eligibility must be checked before asking it for a baseline.
        if (organisationId != target.OrganisationId || registrationId != target.RegistrationId
            || accreditationId != target.AccreditationId || materialType != target.MaterialType
            || year != target.Year)
            return Task.FromResult<IResult>(Results.NotFound());

        return ReadBaseline(target, environment, reExAdapter);
    }

    private static async Task<IResult> ReadBaseline(
        AccreditationChangeSpikeOptions target,
        IHostEnvironment environment,
        IReExApiAdapter reExAdapter)
    {
        if (!environment.IsDevelopment() || !target.Enabled
            || string.IsNullOrWhiteSpace(target.AccreditationId)
            || string.IsNullOrWhiteSpace(target.OrganisationId)
            || string.IsNullOrWhiteSpace(target.RegistrationId)
            || target.Year <= 0
            || string.IsNullOrWhiteSpace(target.Proposal.SiteName)
            || string.IsNullOrWhiteSpace(target.Proposal.SiteAddress)
            || string.IsNullOrWhiteSpace(target.Proposal.Country))
            return Results.NotFound();

        var result = await reExAdapter.GetAccreditationAsync(
            target.OrganisationId, target.RegistrationId, target.MaterialType, target.Year);
        if (!result.IsSuccess)
            return result.IsNotFound ? Results.NotFound() : Results.Problem(statusCode: 502);

        var baseline = result.Value;
        if (baseline is null || baseline.AccreditationId != target.AccreditationId
            || baseline.OrganisationId != target.OrganisationId
            || baseline.MaterialType != target.MaterialType || baseline.Year != target.Year
            || !baseline.IsExporter)
            return Results.NotFound();

        return Results.Ok(new
        {
            target.OrganisationId,
            target.RegistrationId,
            target.AccreditationId,
            target.MaterialType,
            target.Year,
            // Granted is a local demo assumption; the unchanged stub has no authoritative status.
            Status = "Granted",
            baseline.OrganisationName,
            baseline.RegistrationReference,
            baseline.OverseasSites,
            target.Proposal,
        });
    }
}