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
    private static HttpReExApiAdapter BuildSut(
        string organisationJson,
        string overseasSitesJson = "{}"
    )
    {
        var handler = new RoutingHandler(organisationJson, overseasSitesJson);
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

    [Fact]
    public async Task GetAccreditationAsync_ReprocessorRegistration_MapsWasteProcessingTypeAndPostcode()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.WasteProcessingType.Should().Be("reprocessor");
        result.Value!.CompanyRegisterAddressPostcode.Should().Be("AB1 2CD");
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsWasteProcessingTypeAndPostcode()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.WasteProcessingType.Should().Be("exporter");
        result.Value!.CompanyRegisterAddressPostcode.Should().Be("AB1 2CD");
    }

    // RA-424: the frontend shows this in place of the (non-existent) overseas site address on
    // the exporter's accreditation application header/landing page.
    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsCompanyRegisteredAddress()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.CompanyRegisteredAddress.Should().Be("1 Example Hill, Exampleton, AB1 2CD");
    }

    // RA-434: companiesHouseNumber lives on companyDetails and is org-wide, not per-registration.
    [Fact]
    public async Task GetAccreditationAsync_MapsCompaniesHouseNumber()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.CompaniesHouseNumber.Should().Be("09876543");
    }

    // RA-434: only the PermitNumber strings are extracted — a permit with no permitNumber (e.g.
    // a waste exemption) must be dropped rather than surfaced as a null/blank entry.
    [Fact]
    public async Task GetAccreditationAsync_MapsPermitNumbersFromRegistrationOnly()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.PermitNumbers.Should().BeEquivalentTo(["WML123456"]);
    }

    [Fact]
    public async Task GetAccreditationAsync_RegistrationWithNoPermits_ReturnsEmptyPermitNumbers()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.PermitNumbers.Should().BeEmpty();
    }

    // Regression test for RA-424: the real ReEx API sends "up_to_5000" (confirmed by commit
    // c5bdf46, which set this fixture's tonnageBand to "up_to_5000" against a captured
    // production payload), but TonnageBandMap only recognised "up_to_1000" — every real exporter
    // accreditation with this band silently dropped to a null PlannedTonnageBand.
    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsUpTo5000TonnageBand()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.Prns!.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo5000);
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_MapsSeededSitesWithIsNewSiteFalse()
    {
        const string overseasSitesJson = """
            {
              "1": {
                "name": "Overseas Recycling Co",
                "country": "France",
                "address": { "line1": "1 Rue Example", "townOrCity": "Paris" }
              }
            }
            """;
        var sut = BuildSut(OrganisationJson, overseasSitesJson);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        result.Value!.OverseasSites.Should().ContainSingle();
        var site = result.Value!.OverseasSites[0];
        site.SiteId.Should().Be(1);
        site.SiteName.Should().Be("Overseas Recycling Co");
        site.IsNewSite.Should()
            .BeFalse(because: "RA-297: ReEx-seeded sites are the registry, not new sites");
    }

    [Fact]
    public async Task GetAccreditationAsync_ExporterRegistration_NoRegisteredOfficePostcode_FailsRatherThanSubmittingMalformedPayload()
    {
        var sut = BuildSut(OrganisationJsonNoCompanyPostcode);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-exporter-1",
            MaterialType.Aluminium,
            2026
        );

        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be(500);
    }

    [Fact]
    public async Task GetAccreditationAsync_ReprocessorRegistration_NoRegisteredOfficePostcode_StillSucceeds()
    {
        var sut = BuildSut(OrganisationJsonNoCompanyPostcode);

        var result = await sut.GetAccreditationAsync(
            "6a2fcd74e16883c137d01188",
            "reg-reprocessor-1",
            MaterialType.Aluminium,
            2026
        );

        result
            .IsSuccess.Should()
            .BeTrue(
                because: "the registered-office postcode guard only applies to exporters — reprocessors derive their regulator postcode from the site address"
            );
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
            "companiesHouseNumber": "09876543",
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

    // Same as OrganisationJson but companyDetails.address has no postcode key, reproducing
    // the malformed-upstream-data shape from PR review comment
    // DEFRA/epr-register-enrol-backend#64.
    private const string OrganisationJsonNoCompanyPostcode = """
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
        private readonly string _overseasSitesJson;

        public RoutingHandler(string organisationJson, string overseasSitesJson = "{}")
        {
            _organisationJson = organisationJson;
            _overseasSitesJson = overseasSitesJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var isOverseasSites = request.RequestUri!.AbsolutePath.Contains("overseas-sites");
            var body = isOverseasSites ? _overseasSitesJson : _organisationJson;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                }
            );
        }
    }
}
