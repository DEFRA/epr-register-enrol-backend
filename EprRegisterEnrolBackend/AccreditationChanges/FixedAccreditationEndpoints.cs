using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json;

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
        group.MapPost("{organisationId}/{registrationId}/{accreditationId}/{materialType}/{year:int}/submit", Submit);
    }

    private static Task<IResult> ReadConfiguredTarget(
        IOptions<AccreditationChangeSpikeOptions> options,
        IHostEnvironment environment,
        IReExApiAdapter reExAdapter, ChangeRequestPersistence persistence, CancellationToken ct) =>
        ReadBaseline(options.Value, environment, reExAdapter, persistence, ct);

    private static Task<IResult> ReadTarget(
        string organisationId,
        string registrationId,
        string accreditationId,
        MaterialType materialType,
        int year,
        IOptions<AccreditationChangeSpikeOptions> options,
        IHostEnvironment environment,
        IReExApiAdapter reExAdapter, ChangeRequestPersistence persistence, CancellationToken ct)
    {
        var target = options.Value;
        // The development adapter synthesizes accreditation responses for arbitrary inputs.
        // Eligibility must be checked before asking it for a baseline.
        if (organisationId != target.OrganisationId || registrationId != target.RegistrationId
            || accreditationId != target.AccreditationId || materialType != target.MaterialType
            || year != target.Year)
            return Task.FromResult<IResult>(Results.NotFound());

        return ReadBaseline(target, environment, reExAdapter, persistence, ct);
    }

    private static async Task<IResult> ReadBaseline(
        AccreditationChangeSpikeOptions target,
        IHostEnvironment environment,
        IReExApiAdapter reExAdapter, ChangeRequestPersistence persistence, CancellationToken ct)
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

        return TypedResults.Ok(new FixedAccreditationView
        {
            OrganisationId = target.OrganisationId,
            RegistrationId = target.RegistrationId,
            AccreditationId = target.AccreditationId,
            MaterialType = target.MaterialType,
            Year = target.Year,
            OrganisationName = baseline.OrganisationName ?? "",
            RegistrationReference = baseline.RegistrationReference ?? "",
            OverseasSites = baseline.OverseasSites,
            Proposal = target.Proposal,
            ActiveRequest = await persistence.ReadActive(new(target.OrganisationId, target.RegistrationId,
                target.AccreditationId, target.MaterialType, target.Year), ct),
        });
    }

    private static async Task<IResult> Submit(string organisationId, string registrationId,
        string accreditationId, MaterialType materialType, int year,
        IOptions<AccreditationChangeSpikeOptions> options, IHostEnvironment environment,
        IReExApiAdapter reExAdapter, ChangeRequestPersistence persistence,
        ChangeRequestSubmission submission, CancellationToken ct)
    {
        var result = await ReadTarget(organisationId, registrationId, accreditationId, materialType,
            year, options, environment, reExAdapter, persistence, ct);
        if (result is not Ok<FixedAccreditationView> read) return result;
        var baseline = read.Value!;
        var request = await persistence.StartOrResume(new ChangeRequest
        {
            Target = new(organisationId, registrationId, accreditationId, materialType, year),
            OrganisationName = baseline.OrganisationName,
            RegistrationReference = baseline.RegistrationReference,
            BaselineSites = baseline.OverseasSites,
            Proposal = baseline.Proposal,
        }, ct);
        try
        {
            return Results.Ok(await submission.Submit(request, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return Results.Problem("Submission is unconfirmed. Retry the same active request.", statusCode: 503);
        }
    }
}
