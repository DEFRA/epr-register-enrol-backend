using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.ReEx.Http;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.ReEx;

public static class ReExServiceCollectionExtensions
{
    /// <summary>
    /// Registers the ReEx API typed HTTP client with Basic Auth and config binding.
    /// Reads the base URL from the "ReExApi" config section.
    /// Reads credentials from the flat env vars REEX_API_BASIC_AUTH_USERNAME /
    /// REEX_API_BASIC_AUTH_PASSWORD (CDP secret naming — not a nested section).
    /// </summary>
    public static IServiceCollection AddReExClients(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.Configure<ReExConfig>(configuration.GetSection("ReExApi"));

        // Bind flat env-var credentials rather than a nested config section
        services.Configure<ReExCredentials>(creds =>
        {
            creds.Username = configuration["REEX_API_BASIC_AUTH_USERNAME"] ?? string.Empty;
            creds.Password = configuration["REEX_API_BASIC_AUTH_PASSWORD"] ?? string.Empty;
        });

        services.AddTransient<BasicAuthHandler>();

        services
            .AddHttpClient<IReExClient, ReExClient>()
            .AddHttpMessageHandler<BasicAuthHandler>();

        return services;
    }
}
