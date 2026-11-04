using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EprRegisterEnrolBackend.Registrations;

/// <summary>The current status of a registration, as the operator journey's material dashboard pages show it.</summary>
public sealed record RegistrationStatusResponse(
    string RegistrationId,
    string? RegistrationNumber,
    RegistrationStatus Status
);

/// <summary>
/// RA-425: the registration status read by the frontend's material dashboard pages, from the domain model's
/// current registration version. Kept out of the accreditation-application endpoints, which RA-644 is rewriting.
/// </summary>
public static class RegistrationStatusEndpoints
{
    /// <summary>Maps the endpoint only when on; off by default, until the in-year changes journey ships.</summary>
    public const string FeatureFlagKey = "FeatureFlags:InYearChangesEnabled";

    public static IServiceCollection AddRegistrationVersionStore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new RegistrationVersionStore(
            // Resolved on first use, not here: see RegistrationVersionStore.
            () => sp.GetService<IVersionedPersistence<RegistrationEntity>>(),
            sp.GetRequiredService<IMongoDbClientFactory>(),
            sp.GetRequiredService<TimeProvider>()
        ));
        return services;
    }

    public static void UseRegistrationStatusEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("api/v1/registrations/{registrationId}/status", GetStatus)
            .RequireAuthorization(policy =>
                policy
                    .AddAuthenticationSchemes(FrontendAuthenticationHandler.SchemeName)
                    .RequireAuthenticatedUser()
            );

    // registrationId is the ReEx registration id, which is what the frontend's URLs carry.
    private static async Task<IResult> GetStatus(
        string registrationId,
        RegistrationVersionStore store,
        CancellationToken cancellationToken
    )
    {
        var current = await store.FindCurrentByReExRegistrationIdAsync(
            registrationId,
            cancellationToken
        );

        return current is null
            ? Results.NotFound()
            : Results.Ok(
                new RegistrationStatusResponse(
                    registrationId,
                    current.RegistrationNumber,
                    current.Status
                )
            );
    }
}
