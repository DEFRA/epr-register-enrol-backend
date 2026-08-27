using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>
/// <see cref="WebApplicationFactory{Program}"/> for tests that build the full
/// host but never exercise Mongo behaviour (health endpoints, auth-gate
/// integration, config binding). Wires the app onto the assembly fixture's
/// ephemeral mongod so <c>MongoIndexInitializerService</c> — and any request
/// that resolves a Mongo-backed persistence — talks to a real server instead of
/// eating a ~30s server-selection timeout against the unreachable default
/// connection string.
/// </summary>
public sealed class EphemeralMongoTestFactory : WebApplicationFactory<Program>
{
    private readonly MongoIntegrationFixture _fixture;
    private readonly string _databaseName;
    private readonly string? _environment;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    public EphemeralMongoTestFactory(
        MongoIntegrationFixture fixture,
        string databaseNamePrefix,
        string? environment = null,
        IReadOnlyDictionary<string, string?>? settings = null)
    {
        _fixture = fixture;
        _databaseName = MongoIntegrationFixture.NewDatabaseName(databaseNamePrefix);
        _environment = environment;
        _settings = settings ?? new Dictionary<string, string?>();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        builder.ConfigureAppConfiguration(
            (_, config) => config.AddInMemoryCollection(_settings));

        builder.ConfigureServices(services =>
            services.UseEphemeralMongoPersistence(_fixture, _databaseName));
    }
}
