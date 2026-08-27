using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>
/// Helpers for swapping the production Mongo wiring in a
/// <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>-based
/// test for the ephemeral <c>mongod</c> managed by
/// <see cref="MongoIntegrationFixture"/>. Centralised so WebApplicationFactory
/// suites do not silently diverge in how they wire the DI container.
/// </summary>
public static class TestServiceCollectionExtensions
{
    /// <summary>
    /// Point <see cref="IMongoDbClientFactory"/> at <paramref name="fixture"/>'s
    /// ephemeral mongod against a fresh per-test <paramref name="databaseName"/>.
    /// The production persistence classes are used verbatim — only the
    /// connection target is swapped — so their <c>MongoService.EnsureIndexes</c>
    /// runs against a real server instead of eating a server-selection timeout
    /// with no Mongo reachable (which every WebApplicationFactory test would
    /// otherwise pay once MongoIndexInitializerService resolves them at
    /// startup).
    /// </summary>
    public static IServiceCollection UseEphemeralMongoPersistence(
        this IServiceCollection services,
        MongoIntegrationFixture fixture,
        string databaseName)
    {
        services.RemoveAll<IMongoDbClientFactory>();
        services.AddSingleton<IMongoDbClientFactory>(
            new TestMongoDbClientFactory(fixture.ConnectionString, databaseName));

        return services;
    }
}
