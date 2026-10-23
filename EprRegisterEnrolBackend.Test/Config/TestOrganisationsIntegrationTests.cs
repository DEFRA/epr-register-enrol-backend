using System.Net;
using System.Net.Http.Headers;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.Config;

// Proves Program.cs wires TestOrganisations__Enabled end to end outside Development: the prod
// startup guard, the e2e- ReEx routing and the gated mgmt-tests Bearer. The flag is read EAGERLY
// in ConfigureBuilder (it picks which adapter type to register), so - per the gotcha
// ReExUseStubIntegrationTests documents - only a real process environment variable reaches it;
// WebApplicationFactory's in-memory settings arrive too late. ENVIRONMENT is read straight from
// the process environment by design (TestOrganisationsConfig.ReadCdpEnvironment).
[Collection(TestOrganisationsEnvVarMutationCollection.Name)]
public class TestOrganisationsIntegrationTests(MongoIntegrationFixture fixture)
{
    private const string FrontendSecret = "integration-test-frontend-secret";
    private const string MgmtTestsSecret = "integration-test-mgmt-tests-secret";
    private const string EnabledEnvVar = "TestOrganisations__Enabled";
    private const string CdpEnvironmentEnvVar = "ENVIRONMENT";

    [Fact]
    public async Task Enabled_MgmtTestsBearer_PassesFrontendAuth()
    {
        await WithEnvironment(
            enabled: "true",
            cdpEnvironment: "test",
            async () =>
            {
                await using var factory = MakeFactory("test_orgs_mgmt_bearer_on");
                using var client = ClientWithBearer(factory, MgmtTestsSecret);

                var response = await client.GetAsync(
                    "/api/v1/accreditation-applications/e2e-0a1b2c",
                    TestContext.Current.CancellationToken
                );

                response.StatusCode.Should().Be(HttpStatusCode.OK);
            }
        );
    }

    [Fact]
    public async Task Disabled_MgmtTestsBearer_IsRejectedEvenWithTheSecretSet()
    {
        await WithEnvironment(
            enabled: null,
            cdpEnvironment: "test",
            async () =>
            {
                await using var factory = MakeFactory("test_orgs_mgmt_bearer_off");
                using var client = ClientWithBearer(factory, MgmtTestsSecret);

                var response = await client.GetAsync(
                    "/api/v1/accreditation-applications/e2e-0a1b2c",
                    TestContext.Current.CancellationToken
                );

                response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
        );
    }

    [Fact]
    public async Task Enabled_TestOrganisation_IsAnsweredByTheStub_RealOrganisationIsNot()
    {
        await WithEnvironment(
            enabled: "true",
            cdpEnvironment: "test",
            async () =>
            {
                await using var factory = MakeFactory("test_orgs_routing");
                using var client = ClientWithBearer(factory, FrontendSecret);

                var testOrg = await client.GetAsync(
                    "/api/v1/organisations/e2e-0a1b2c/defra-link",
                    TestContext.Current.CancellationToken
                );
                // 50001 exists only in the stub's fixtures; no real ReExApi__BaseUrl is
                // configured, so it must NOT resolve - it went to HttpReExApiAdapter.
                var realOrg = await client.GetAsync(
                    "/api/v1/organisations/50001/defra-link",
                    TestContext.Current.CancellationToken
                );

                testOrg.StatusCode.Should().Be(HttpStatusCode.OK);
                realOrg.StatusCode.Should().NotBe(HttpStatusCode.OK);
            }
        );
    }

    [Fact]
    public async Task Disabled_TestOrganisation_GoesToRealReEx()
    {
        await WithEnvironment(
            enabled: "false",
            cdpEnvironment: "test",
            async () =>
            {
                await using var factory = MakeFactory("test_orgs_routing_off");
                using var client = ClientWithBearer(factory, FrontendSecret);

                var response = await client.GetAsync(
                    "/api/v1/organisations/e2e-0a1b2c/defra-link",
                    TestContext.Current.CancellationToken
                );

                response.StatusCode.Should().NotBe(HttpStatusCode.OK);
            }
        );
    }

    [Fact]
    public async Task Enabled_InCdpProd_RefusesToStart()
    {
        await WithEnvironment(
            enabled: "true",
            cdpEnvironment: "prod",
            () =>
            {
                using var factory = MakeFactory("test_orgs_prod_guard");

                var act = () => factory.CreateClient();

                act.Should()
                    .Throw<InvalidOperationException>()
                    .WithMessage("*TestOrganisations__Enabled*prod*");
                return Task.CompletedTask;
            }
        );
    }

    private EphemeralMongoTestFactory MakeFactory(string databaseNamePrefix) =>
        new(
            fixture,
            databaseNamePrefix,
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:FRONTEND"] = FrontendSecret,
                ["AUTH_SHARED_SECRET:MGMT_TESTS"] = MgmtTestsSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-test-case-management-secret",
                ["CaseWorking:UseStub"] = "true",
            }
        );

    private static HttpClient ClientWithBearer(EphemeralMongoTestFactory factory, string secret)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            secret
        );
        return client;
    }

    private static async Task WithEnvironment(
        string? enabled,
        string? cdpEnvironment,
        Func<Task> body
    )
    {
        var previousEnabled = Environment.GetEnvironmentVariable(EnabledEnvVar);
        var previousCdpEnvironment = Environment.GetEnvironmentVariable(CdpEnvironmentEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnabledEnvVar, enabled);
            Environment.SetEnvironmentVariable(CdpEnvironmentEnvVar, cdpEnvironment);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnabledEnvVar, previousEnabled);
            Environment.SetEnvironmentVariable(CdpEnvironmentEnvVar, previousCdpEnvironment);
        }
    }
}

/// <summary>
/// Disables parallelization for tests that mutate the process-global
/// TestOrganisations__Enabled and ENVIRONMENT variables.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TestOrganisationsEnvVarMutationCollection
{
    public const string Name = "test-organisations-env-var-mutation";
}
