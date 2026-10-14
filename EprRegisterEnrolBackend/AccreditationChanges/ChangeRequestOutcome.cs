using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationChanges;

public sealed record ChangeRequestOutcome(string RequestId, string CaseId, string ProposedSiteId,
    AccreditationChangeTarget Target, string Outcome);

public static class ChangeRequestOutcomeEndpoints
{
    public static void UseChangeRequestOutcomeEndpoints(this IEndpointRouteBuilder app) =>
        app.MapPost("api/v1/accreditation-changes/outcome", async (ChangeRequestOutcome outcome,
            ChangeRequestPersistence persistence, IOptions<AccreditationChangeSpikeOptions> options,
            IHostEnvironment environment, CancellationToken ct) =>
        {
            if (!environment.IsDevelopment() || !options.Value.Enabled) return Results.NotFound();
            if (outcome.Outcome != "rejected" || outcome.Target is null
                || !Guid.TryParse(outcome.RequestId, out _) || !Guid.TryParse(outcome.CaseId, out _)
                || !Guid.TryParse(outcome.ProposedSiteId, out _))
                return Results.BadRequest();
            if (await persistence.Read(outcome.RequestId, ct) is null) return Results.NotFound();
            return await persistence.Refuse(outcome, ct) ? Results.Ok() : Results.Conflict();
        }).RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(CaseManagementAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
}
