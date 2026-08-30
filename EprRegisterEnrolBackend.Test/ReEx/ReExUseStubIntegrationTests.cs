using System.Net;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.ReEx;

// Proves ReExApi:UseStub actually wires up StubReExApiAdapter/FakeOrganisationPersistence
// outside Development (Program.cs), the way perf-test is meant to run.
//
// The adapter choice in Program.cs is decided by an EAGER, synchronous config read (needed
// because it picks which type to register as a singleton, unlike the deferred
// Configure<T>(...) delegates elsewhere) — so, per the same gotcha
// CaseManagementAuthConfigEnvVarBindingTests documents, only a REAL process environment
// variable proves it (WebApplicationFactory's ConfigureAppConfiguration/AddInMemoryCollection
// settings are appended too late for this particular eager read to observe them).
[Collection(ReExUseStubEnvVarMutationCollection.Name)]
public class ReExUseStubIntegrationTests(MongoIntegrationFixture fixture)
{
    private const string ValidFrontendSecret = "integration-test-frontend-secret";
    private const string EnvVarName = "ReExApi__UseStub";

    [Fact]
    public async Task DefraLink_ReturnsStubData_WhenUseStubTrueOutsideDevelopment()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, "true");
            await using var factory = MakeFactory("reex_use_stub_true");
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer",
                    ValidFrontendSecret
                );

            // Org 50001 only exists in FakeOrganisationPersistence's fixtures — the real
            // ReEx API (which this test never configures a base URL for) has no concept
            // of it.
            var response = await client.GetAsync(
                "/api/v1/organisations/50001/defra-link",
                TestContext.Current.CancellationToken
            );

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, previous);
        }
    }

    [Fact]
    public async Task DefraLink_DoesNotReturnStubData_WhenUseStubFalseOutsideDevelopment()
    {
        var previous = Environment.GetEnvironmentVariable(EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(EnvVarName, "false");
            await using var factory = MakeFactory("reex_use_stub_false");
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer",
                    ValidFrontendSecret
                );

            var response = await client.GetAsync(
                "/api/v1/organisations/50001/defra-link",
                TestContext.Current.CancellationToken
            );

            // No real ReExApi__BaseUrl is configured, so the real HttpReExApiAdapter can't
            // reach anything — the point here is only that it does NOT resolve org 50001
            // from the in-memory fixtures the way the UseStub=true case does.
            response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVarName, previous);
        }
    }

    private EphemeralMongoTestFactory MakeFactory(string databaseNamePrefix) =>
        new(
            fixture,
            databaseNamePrefix,
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:FRONTEND"] = ValidFrontendSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-test-case-management-secret",
                ["CaseWorking:UseStub"] = "true",
            }
        );
}

/// <summary>
/// Collection that disables parallelization for tests that mutate the process-global
/// ReExApi__UseStub environment variable.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReExUseStubEnvVarMutationCollection
{
    public const string Name = "reex-use-stub-env-var-mutation";
}
