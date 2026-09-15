using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-580/RA-580-1: end-to-end coverage for the seam AccreditationApplicationEndpointsTests
/// deliberately skips — those tests mock IReExApiAdapter wholesale, so they never prove the
/// real HttpReExApiAdapter/ReExClient mapping logic actually works when driven from the real
/// POST .../seed HTTP endpoint against real-shaped ReEx JSON. These tests use the real adapter
/// and client, faking only the ReEx HTTP transport (SeedRealReExWiringTestFactory.FakeReExHandler).
/// </summary>
public class SeedRealReExWiringTests : IClassFixture<SeedRealReExWiringTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SeedRealReExWiringTestFactory _factory;
    private readonly HttpClient _client;

    public SeedRealReExWiringTests(SeedRealReExWiringTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
        _factory.FakeReExHandler.Reset();
    }

    [Fact]
    public async Task Seed_ExporterWithMixOfApprovedAndUnapprovedSites_DerivesSelectedFromValidFrom()
    {
        Reset();
        _factory.FakeReExHandler.OrganisationJson = OrganisationJson;
        // RA-580-1: ORS-A alone returns every site on the registration, approved and
        // unapproved together — "001" has no validFrom (registered but not yet accredited),
        // "002" does (accredited).
        _factory.FakeReExHandler.AccreditationSitesJson = """
            {
              "001": { "name": "Registered Only Co", "country": "France", "address": { "line1": "1 Rue Example", "townOrCity": "Paris" }, "validFrom": null },
              "002": { "name": "Accredited Co", "country": "Spain", "address": { "line1": "New Address", "townOrCity": "Madrid" }, "validFrom": "2024-01-01T00:00:00.000Z" }
            }
            """;

        // Accreditation's validFrom in OrganisationJson is 2026-01-01, and Seed asks the
        // adapter for (Year - 1), so Year must be 2027 for the year check to pass.
        var request = new SeedRequest { Year = 2027 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/6a2fcd74e16883c137d01188/reg-exporter-1/Aluminium/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                because: await response.Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken
                )
            );
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );

        body!.IsExporter.Should().BeTrue();
        body.OverseasSites.Should().NotBeNull();
        body.OverseasSites!.Sites.Should().HaveCount(2);

        var byOrsId = body.OverseasSites.Sites.ToDictionary(s => s.OrsId!);

        byOrsId["001"]
            .Selected.Should()
            .BeFalse(because: "001 has no ValidFrom, so it's not yet accredited");
        byOrsId["001"].SiteName.Should().Be("Registered Only Co");
        byOrsId["001"].ValidFrom.Should().BeNull();

        byOrsId["002"].Selected.Should().BeTrue(because: "002 has a ValidFrom, so it's accredited");
        byOrsId["002"].SiteName.Should().Be("Accredited Co");
        byOrsId["002"].ValidFrom.Should().Be("2024-01-01T00:00:00.000Z");
        byOrsId["002"].Country.Should().Be("Spain");
    }

    [Fact]
    public async Task Seed_ReprocessorRegistration_ReturnsNullOverseasSitesAndNeverCallsOverseasSitesEndpoint()
    {
        Reset();
        _factory.FakeReExHandler.OrganisationJson = OrganisationJson;

        var request = new SeedRequest { Year = 2027 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/6a2fcd74e16883c137d01188/reg-reprocessor-1/Aluminium/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response
            .StatusCode.Should()
            .Be(
                HttpStatusCode.Created,
                because: await response.Content.ReadAsStringAsync(
                    TestContext.Current.CancellationToken
                )
            );
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );

        body!.IsExporter.Should().BeFalse();
        body.OverseasSites.Should().BeNull();
        _factory
            .FakeReExHandler.RequestedPaths.Should()
            .NotContain(
                path => path.Contains("overseas-sites"),
                because: "a reprocessor registration has no overseas sites to fetch"
            );
    }

    [Fact]
    public async Task Seed_ReExAccreditationYearMismatch_PropagatesNotFoundThroughTheRealAdapter()
    {
        Reset();
        _factory.FakeReExHandler.OrganisationJson = OrganisationJson;

        // OrganisationJson's exporter accreditation validFrom is 2026-01-01, so asking for
        // 2028 (adapter receives Year - 1 = 2028) can never match — proving a real adapter
        // failure genuinely propagates all the way out through the Seed endpoint as 404,
        // not just in the adapter-level unit tests.
        var request = new SeedRequest { Year = 2029 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/6a2fcd74e16883c137d01188/reg-exporter-1/Aluminium/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private const string OrganisationJson = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor", "exporter"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "tradingName": "Test Recycling Solutions Ltd",
            "companiesHouseNumber": "09876543",
            "address": {
              "line1": "1 Example Hill",
              "postcode": "AB1 2CD",
              "country": "UK",
              "town": "Exampleton"
            }
          },
          "submitterContactDetails": {
            "fullName": "Barton Deckow",
            "email": "REEXServiceTeam@defra.gov.uk",
            "phone": "0111 478 4919",
            "jobTitle": "Human Infrastructure Architect"
          },
          "submittedToRegulator": "ea",
          "registrations": [
            {
              "id": "reg-reprocessor-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "site": {
                "address": {
                  "line1": "Reprocessor Site Road",
                  "postcode": "HU7 7BX",
                  "country": "UK",
                  "town": "Exampleton"
                },
                "gridReference": "TQ 132 546"
              },
              "cbduNumber": "CBDU663848",
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "wasteManagementPermits": [
                { "type": "environmental_permit", "permitNumber": "WML123456" },
                { "type": "waste_exemption" }
              ],
              "accreditationId": "acc-reprocessor-1",
              "registrationNumber": "R25SR500000912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "reprocessingType": "input",
              "status": "approved",
              "accreditation": null
            },
            {
              "id": "reg-exporter-1",
              "submittedToRegulator": "ea",
              "orgName": "Test Recycling Solutions Ltd",
              "noticeAddress": {
                "fullAddress": "1 Example Parade, Example Town",
                "country": "UK"
              },
              "cbduNumber": "CBDU506923",
              "material": "aluminium",
              "exportPorts": ["Southampton", "Portsmouth"],
              "wasteProcessingType": "exporter",
              "accreditationId": "acc-exporter-1",
              "registrationNumber": "E25SR500020912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "status": "approved",
              "accreditation": null
            }
          ],
          "accreditations": [
            {
              "id": "acc-reprocessor-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "reprocessor",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "over_10000",
                "signatories": [
                  { "fullName": "Test Signatory", "email": "signatory@example.test", "phone": "0111 000 0002", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": []
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "R-ACC12045AL",
              "reprocessingType": "input",
              "status": "approved"
            },
            {
              "id": "acc-exporter-1",
              "submittedToRegulator": "ea",
              "wasteProcessingType": "exporter",
              "material": "aluminium",
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "up_to_5000",
                "signatories": [
                  { "fullName": "Test Exporter Signatory", "email": "exporter.signatory@example.test", "phone": "1234567890", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": []
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "E-ACC12245AL",
              "status": "approved"
            }
          ]
        }
        """;
}
