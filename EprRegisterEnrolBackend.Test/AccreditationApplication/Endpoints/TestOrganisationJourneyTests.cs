using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-000: the exact HTTP sequence epr-register-enrol-mgmt-tests drives against this API to
// create a work item backed by this service ("Path B") for an e2e- test organisation - seed,
// complete every reprocessor section, submit, read caseManagementWorkItemId back - run against
// the REAL StubReExApiAdapter (Development registers it for everything) and a real ephemeral
// Mongo, so a change that breaks the recipe the E2E suite depends on fails here first. Case
// Management is the stub adapter: this proves this service's side of the contract only.
public class TestOrganisationJourneyTests(MongoIntegrationFixture fixture)
{
    private const string Simulated = "simulate-delivered@notifications.service.gov.uk";

    [Fact]
    public async Task SeedCompleteSubmit_ForATestOrganisation_ExposesTheWorkItemId()
    {
        await using var factory = new EphemeralMongoTestFactory(
            fixture,
            "test_org_journey",
            Environments.Development
        ).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<ICaseWorkingApiAdapter, StubCaseWorkingApiAdapter>()
            )
        );
        using var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        var hex = Guid.NewGuid().ToString("N")[..12];
        var org = $"e2e-{hex}";
        var baseUrl = "/api/v1/accreditation-applications";

        var seed = await client.PostAsJsonAsync(
            $"{baseUrl}/{org}/e2e-reg-{hex}/Plastic/seed",
            new { year = 2026 },
            ct
        );
        seed.StatusCode.Should().Be(HttpStatusCode.Created, await seed.Content.ReadAsStringAsync(ct));
        var appId = (await ReadJson(seed)).GetProperty("applicationId").GetString();
        var app = $"{baseUrl}/{org}/{appId}";

        await Patch(
            client,
            $"{app}/prns",
            new
            {
                plannedTonnageBand = "UpTo5000",
                authorisers = new[] { new { fullName = "E2E Authoriser", email = Simulated } },
                sectionStatus = "Completed",
            }
        );
        await Patch(
            client,
            $"{app}/business-plan",
            new
            {
                newInfrastructurePercent = 100,
                priceSupportPercent = 0,
                businessCollectionsPercent = 0,
                communicationsPercent = 0,
                newMarketsPercent = 0,
                newUsesPercent = 0,
                otherPercent = 0,
                newInfrastructureDetail = "E2E detail",
                sectionStatus = "Completed",
            }
        );
        await Patch(
            client,
            $"{app}/sampling-plan",
            new
            {
                files = new[]
                {
                    new
                    {
                        fileId = $"e2e-file-{hex}",
                        filename = "sampling-plan.pdf",
                        contentType = "application/pdf",
                        uploadedByUserId = "e2e-tests",
                        scanStatus = "Clean",
                        documentType = "SamplingPlan",
                        s3Key = $"e2e/{hex}/sampling-plan.pdf",
                        s3Bucket = "sampling-plans",
                    },
                },
                sectionStatus = "Completed",
            }
        );

        var submit = await client.PostAsJsonAsync(
            $"{app}/submit",
            new
            {
                fullName = "E2E Submitter",
                jobTitle = "Tester",
                email = Simulated,
                paymentReference = $"E2E-{hex}",
            },
            ct
        );
        submit.StatusCode.Should().Be(HttpStatusCode.OK, await submit.Content.ReadAsStringAsync(ct));

        var read = await ReadJson(await client.GetAsync(app, ct));
        read.GetProperty("applicationStatus").GetString().Should().Be("Submitted");
        read.GetProperty("caseManagementWorkItemId").GetGuid().Should().NotBeEmpty();
        read.GetProperty("organisationName").GetString().Should().Be($"E2E Organisation {org}");
        read.GetProperty("nation").GetString().Should().Be("England");
        read.GetProperty("orgId").GetInt32().Should().BeInRange(900_000, 999_999);
    }

    private static async Task Patch(HttpClient client, string url, object body)
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PatchAsJsonAsync(url, body, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken
        ));
}
