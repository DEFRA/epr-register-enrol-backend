using System.Net;
using System.Net.Http.Json;
using System.Text;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.ReEx.Dtos;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.ReEx;

/// <summary>
/// Round-trip deserialization test against a redacted real ReEx API response (PII replaced
/// with placeholders, structure and field shapes preserved exactly). Exists to catch DTO/API
/// contract mismatches in one shot instead of one production incident at a time — see
/// docs/reex-dto-mismatches.md for the mismatches this fixture was built to catch.
/// </summary>
public class ReExOrganisationFixtureTests
{
    private static ReExClient BuildSut(string responseBody)
    {
        var handler = new RawStringHandler(HttpStatusCode.OK, responseBody);
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExConfig { BaseUrl = "http://localhost:5000/" });
        return new ReExClient(httpClient, config, EnabledNullLogger<ReExClient>.Instance);
    }

    [Fact]
    public async Task GetOrganisationsAsync_RealisticOrganisationPayload_DeserializesEveryField()
    {
        var sut = BuildSut(OrganisationJson);

        var result = await sut.GetOrganisationsAsync(
            "6a2fcd74e16883c137d01188",
            TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue(because: result.Error?.Message);
        var org = result.Value!;

        // ── Top-level org fields ────────────────────────────────────────────
        org.Id.Should().Be("6a2fcd74e16883c137d01188");
        org.SchemaVersion.Should().Be(3);
        org.OrgId.Should().Be(509193);
        org.WasteProcessingTypes.Should().Contain("reprocessor");
        org.ReprocessingNations.Should().Contain("england");
        org.BusinessType.Should().Be("individual");
        org.SubmittedToRegulator.Should().Be("ea");
        org.LinkedDefraOrganisation!.OrgId.Should().Be("67b9e8fc-2235-431a-a7b9-80663c81b6ff");

        org.CompanyDetails!.Name.Should().Be("Test Recycling Solutions Ltd");
        org.CompanyDetails.CompaniesHouseNumber.Should().Be("09876543");
        org.CompanyDetails.Address!.Line1.Should().Be("1 Example Hill");
        org.CompanyDetails.Address.Postcode.Should().Be("AB1 2CD");

        // RA-526: registeredAddress and address are distinct fields, populated independently.
        org.CompanyDetails.RegisteredAddress!.Line1.Should().Be("1 Registered Office Row");
        org.CompanyDetails.RegisteredAddress.Postcode.Should().Be("RO1 1RO");

        org.SubmitterContactDetails!.FullName.Should().Be("Test Submitter");
        org.SubmitterContactDetails.Email.Should().Be("submitter@example.test");
        org.SubmitterContactDetails.JobTitle.Should().Be("Sustainability Director");

        org.ManagementContactDetails!.JobTitle.Should().Be("Compliance Manager");

        // ── Reprocessor registration ────────────────────────────────────────
        var reprocessor = org
            .Registrations.Should()
            .ContainSingle(r => r is ReprocessorRegistrationDto)
            .Subject.Should()
            .BeOfType<ReprocessorRegistrationDto>()
            .Subject;

        reprocessor.Id.Should().Be("reg-reprocessor-1");
        reprocessor.AccreditationId.Should().Be("acc-reprocessor-1");
        reprocessor.RegistrationNumber.Should().Be("R25SR500000912AL");
        reprocessor.SubmittedToRegulator.Should().Be("ea");
        reprocessor.Site!.Address!.Line1.Should().Be("Reprocessor Site Road");
        reprocessor.Site.GridReference.Should().Be("TQ 132 546");

        var permit = reprocessor
            .WasteManagementPermits.Should()
            .ContainSingle(p => p.Type == "environmental_permit")
            .Subject;
        permit.AuthorisedMaterials.Should().HaveCount(2);
        permit.AuthorisedMaterials[0].Material.Should().Be("aluminium");
        permit.AuthorisedMaterials[0].AuthorisedWeightInTonnes.Should().Be(10);
        permit.AuthorisedMaterials[0].TimeScale.Should().Be("yearly");

        var exemptionPermit = reprocessor
            .WasteManagementPermits.Should()
            .ContainSingle(p => p.Type == "waste_exemption")
            .Subject;
        exemptionPermit.Exemptions[0].Reference.Should().Be("WEX123456");
        exemptionPermit.Exemptions[0].ExemptionCode.Should().Be("U9");

        reprocessor.YearlyMetrics.Should().ContainSingle();
        var metric = reprocessor.YearlyMetrics[0];
        metric.Year.Should().Be(2024);
        metric.Input!.UkPackagingWaste.Should().Be(12);
        metric.Input.NonUkPackagingWaste.Should().Be(10);
        metric.Output!.SentToAnotherSite.Should().Be(11);
        metric.Output.Contaminants.Should().Be(11);

        // ── Exporter registration ───────────────────────────────────────────
        var exporter = org
            .Registrations.Should()
            .ContainSingle(r => r is ExporterRegistrationDto)
            .Subject.Should()
            .BeOfType<ExporterRegistrationDto>()
            .Subject;

        exporter.AccreditationId.Should().Be("acc-exporter-1");
        exporter.RegistrationNumber.Should().Be("E25SR500020912AL");
        exporter.NoticeAddress!.FullAddress.Should().Be("1 Example Parade, Example Town");
        exporter.ExportPorts.Should().BeEquivalentTo(["Southampton", "Portsmouth"]);
        exporter.OrsFileUploads.Should().ContainSingle();
        exporter
            .OrsFileUploads[0]
            .DefraFormUploadedFileId.Should()
            .Be("00e85b5b-e88f-4d97-afc7-0a985803ab3b");
        exporter.OrsFileUploads[0].DefraFormUserDownloadLink.Should().NotBeNullOrEmpty();
        exporter.OverseasSites.Should().ContainKey("100");
        exporter.OverseasSites["100"].OverseasSiteId.Should().Be("overseas-site-1");

        // ── Accreditations ───────────────────────────────────────────────────
        var reprocessorAccreditation = org
            .Accreditations.Should()
            .ContainSingle(a => a.Id == "acc-reprocessor-1")
            .Subject;
        reprocessorAccreditation.Material.Should().Be("aluminium");
        reprocessorAccreditation.ValidFrom.Should().Be("2026-01-01");
        reprocessorAccreditation.ValidTo.Should().Be("2027-01-01");
        reprocessorAccreditation.PrnIssuance!.TonnageBand.Should().Be("over_10000");
        reprocessorAccreditation.PrnIssuance.Signatories.Should().ContainSingle();
        var businessPlanItem = reprocessorAccreditation
            .PrnIssuance.IncomeBusinessPlan.Should()
            .ContainSingle(i => i.UsageDescription == "Support for business collections")
            .Subject;
        businessPlanItem.PercentIncomeSpent.Should().Be(91);

        var exporterAccreditation = org
            .Accreditations.Should()
            .ContainSingle(a => a.Id == "acc-exporter-1")
            .Subject;
        exporterAccreditation.ValidFrom.Should().Be("2026-01-01");
        exporterAccreditation.ValidTo.Should().Be("2027-01-01");
    }

    private const string OrganisationJson = """
        {
          "id": "6a2fcd74e16883c137d01188",
          "schemaVersion": 3,
          "orgId": 509193,
          "wasteProcessingTypes": ["reprocessor"],
          "reprocessingNations": ["england"],
          "businessType": "individual",
          "companyDetails": {
            "name": "Test Recycling Solutions Ltd",
            "tradingName": "Test Recycling Solutions Ltd",
            "companiesHouseNumber": "09876543",
            "registeredAddress": {
              "line1": "1 Registered Office Row",
              "postcode": "RO1 1RO",
              "country": "UK",
              "town": "Registertown"
            },
            "address": {
              "line1": "1 Example Hill",
              "postcode": "AB1 2CD",
              "country": "UK",
              "town": "Exampleton"
            }
          },
          "submitterContactDetails": {
            "fullName": "Test Submitter",
            "email": "submitter@example.test",
            "phone": "0111 000 0000",
            "jobTitle": "Sustainability Director"
          },
          "managementContactDetails": {
            "fullName": "Test Manager",
            "email": "manager@example.test",
            "phone": "0111 000 0001",
            "jobTitle": "Compliance Manager"
          },
          "submittedToRegulator": "ea",
          "linkedDefraOrganisation": {
            "orgId": "67b9e8fc-2235-431a-a7b9-80663c81b6ff"
          },
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
              "noticeAddress": {
                "line1": "13 Example Garth",
                "postcode": "HU7 7BX",
                "country": "UK",
                "town": "Exampleton"
              },
              "cbduNumber": "CBDU663848",
              "material": "aluminium",
              "wasteProcessingType": "reprocessor",
              "wasteManagementPermits": [
                {
                  "type": "environmental_permit",
                  "permitNumber": "EPR/AB5559210207A001",
                  "authorisedMaterials": [
                    { "material": "aluminium", "authorisedWeightInTonnes": 10, "timeScale": "yearly" },
                    { "material": "fibre", "authorisedWeightInTonnes": 10, "timeScale": "yearly" }
                  ]
                },
                {
                  "type": "waste_exemption",
                  "exemptions": [
                    { "reference": "WEX123456", "exemptionCode": "U9", "materials": ["paper", "plastic"] }
                  ]
                }
              ],
              "yearlyMetrics": [
                {
                  "year": 2024,
                  "input": {
                    "type": "estimated",
                    "ukPackagingWasteInTonnes": 12,
                    "nonUkPackagingWasteInTonnes": 10,
                    "nonPackagingWasteInTonnes": 10
                  },
                  "output": {
                    "type": "estimated",
                    "sentToAnotherSiteInTonnes": 11,
                    "contaminantsInTonnes": 11,
                    "processLossInTonnes": 11
                  }
                }
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
              "wasteManagementPermits": [
                { "type": "installation_permit" }
              ],
              "orsFileUploads": [
                {
                  "defraFormUploadedFileId": "00e85b5b-e88f-4d97-afc7-0a985803ab3b",
                  "defraFormUserDownloadLink": "https://forms-designer.test.cdp-int.defra.cloud/file-download/00e85b5b-e88f-4d97-afc7-0a985803ab3b"
                }
              ],
              "accreditationId": "acc-exporter-1",
              "registrationNumber": "E25SR500020912AL",
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "overseasSites": {
                "100": { "overseasSiteId": "overseas-site-1" }
              },
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
              "site": {
                "address": { "line1": "78 Example Place", "postcode": "HU7 7BX" }
              },
              "orgName": "Test Recycling Solutions Ltd",
              "prnIssuance": {
                "tonnageBand": "over_10000",
                "signatories": [
                  { "fullName": "Test Signatory", "email": "signatory@example.test", "phone": "0111 000 0002", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": [
                  {
                    "percentIncomeSpent": 91,
                    "usageDescription": "Support for business collections",
                    "detailedExplanation": "More detail for spend on new reprocessing infrastructure"
                  }
                ]
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
              "orgName": "EuroPack Example GmbH",
              "prnIssuance": {
                "tonnageBand": "up_to_5000",
                "signatories": [
                  { "fullName": "Test Exporter Signatory", "email": "exporter.signatory@example.test", "phone": "1234567890", "jobTitle": "Director" }
                ],
                "incomeBusinessPlan": [
                  {
                    "percentIncomeSpent": 21,
                    "usageDescription": "New reprocessing infrastructure and maintaining existing infrastructure",
                    "detailedExplanation": "More detail for spend on new reprocessing infrastructure"
                  }
                ]
              },
              "validFrom": "2026-01-01",
              "validTo": "2027-01-01",
              "accreditationNumber": "E-ACC12245AL",
              "status": "approved"
            }
          ]
        }
        """;

    private sealed class RawStringHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RawStringHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                }
            );
    }
}
