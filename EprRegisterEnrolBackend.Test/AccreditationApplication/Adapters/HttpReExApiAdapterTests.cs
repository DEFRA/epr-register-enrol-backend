using System.Net;
using System.Text;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Config;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

/// <summary>
/// Regression coverage for RA-334: HttpReExApiAdapter.GetAccreditationAsync used to source
/// RegistrationReference from org.CompanyDetails.RegistrationNumber, which the real ReEx API
/// never populates — the actual EPR registration number lives per-registration, under
/// registrations[].registrationNumber. Every other test that exercises accreditation submission
/// mocks IReExApiAdapter entirely, so nothing previously exercised this adapter's own mapping
/// logic against realistic ReEx JSON shape.
/// </summary>
public class HttpReExApiAdapterTests
{
    private static HttpReExApiAdapter BuildSut(string organisationJson)
    {
        var handler = new RoutingHandler(organisationJson);
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExConfig { BaseUrl = "http://localhost:5000/" });
        var reExClient = new ReExClient(httpClient, config, NullLogger<ReExClient>.Instance);
        return new HttpReExApiAdapter(reExClient, NullLogger<HttpReExApiAdapter>.Instance);
    }

    [Fact]
    public async Task GetAccreditationAsync_ReprocessorRegistration_ReturnsRegistrationLevelRegistrationNumber()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationReference.Should().Be("R25SR500000912AL");
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_ReturnsRegistrationLevelRegistrationNumber()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.RegistrationReference.Should().Be("E25SR500020912AL");
    }

    // Realistic redacted ReEx organisation payload — companyDetails deliberately has no
    // registrationNumber key, matching the real API. Mirrors the fixture used in
    // ReExOrganisationFixtureTests.cs.
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
            "address": {
              "line1": "1 Example Hill",
              "postcode": "AB1 2CD",
              "country": "UK",
              "town": "Exampleton"
            }
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

    // Returns the organisation payload for the organisations endpoint, and an empty
    // overseas-sites dictionary for the overseas-sites endpoint the adapter calls for
    // exporter registrations — a single fixed body can't serve both shapes.
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly string _organisationJson;

        public RoutingHandler(string organisationJson)
        {
            _organisationJson = organisationJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var isOverseasSites = request.RequestUri!.AbsolutePath.Contains("overseas-sites");
            var body = isOverseasSites ? "{}" : _organisationJson;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
