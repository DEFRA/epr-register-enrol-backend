using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.ReEx.Config;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public static class ReExApiAdapterRegistration
{
    /// <summary>
    /// Chooses the <see cref="IReExApiAdapter"/> implementation.
    /// <list type="bullet">
    /// <item>Development, or <c>ReExApi:UseStub</c> (default false): the stub for everything.
    /// UseStub lets a deployed non-Development environment (e.g. perf-test) opt into the stub
    /// without flipping ASPNETCORE_ENVIRONMENT, which would also pull in unrelated dev-only
    /// surface area.</item>
    /// <item>Otherwise, with <c>TestOrganisations:Enabled</c>: <see cref="TestOrganisationReExApiAdapter"/>,
    /// routing <c>e2e-</c> organisations to the stub and everything else to real ReEx.</item>
    /// <item>Otherwise: real ReEx only.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddReExApiAdapter(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        bool testOrganisationsEnabled
    )
    {
        var reExConfig = new ReExConfig();
        configuration.GetSection("ReExApi").Bind(reExConfig);

        if (environment.IsDevelopment() || reExConfig.UseStub)
        {
            services.AddSingleton<IReExApiAdapter, StubReExApiAdapter>();
            // Fixtures for StubReExApiAdapter's responses — not tied to any persistence
            // interface, the stub is its only consumer.
            services.AddSingleton<FakeOrganisationPersistence>();
            return services;
        }

        if (!testOrganisationsEnabled)
        {
            services.AddSingleton<IReExApiAdapter, HttpReExApiAdapter>();
            return services;
        }

        services.AddSingleton<FakeOrganisationPersistence>();
        services.AddSingleton<StubReExApiAdapter>();
        services.AddSingleton<HttpReExApiAdapter>();
        services.AddSingleton<IReExApiAdapter>(sp => new TestOrganisationReExApiAdapter(
            sp.GetRequiredService<HttpReExApiAdapter>(),
            sp.GetRequiredService<StubReExApiAdapter>()
        ));
        return services;
    }
}
