using System.Net;
using System.Net.Http.Json;
using System.Text;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.ReEx.Config;
using EprRegisterEnrolBackend.ReEx.Dtos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Test.ReEx;

public class ReExClientTests
{
    private static ReExClient BuildSut(HttpMessageHandler handler, string baseUrl = "http://localhost:5000/")
    {
        var httpClient = new HttpClient(handler);
        var config = Options.Create(new ReExConfig { BaseUrl = baseUrl });
        return new ReExClient(httpClient, config, NullLogger<ReExClient>.Instance);
    }

    // ── Success path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrganisationsAsync_200_ReturnsSuccessResult()
    {
        const string json = """
            {
              "id": "org-1",
              "schemaVersion": 3,
              "orgId": 1234,
              "wasteProcessingTypes": ["paper"],
              "reprocessingNations": ["England"],
              "registrations": [],
              "accreditations": []
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("1234");

        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be(200);
        result.Error.Should().BeNull();
        result.Value!.Id.Should().Be("org-1");
        result.Value.SchemaVersion.Should().Be(3);
        result.Value.OrgId.Should().Be(1234);
    }

    [Fact]
    public async Task GetOrganisationsAsync_NumericOrgId_DeserializesCorrectly()
    {
        const string json = """
            {
              "id": "org-1",
              "schemaVersion": 3,
              "orgId": 987654,
              "registrations": [],
              "accreditations": []
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("987654");

        result.IsSuccess.Should().BeTrue();
        result.Value!.OrgId.Should().Be(987654);
    }

    [Fact]
    public async Task GetOverseasSiteAsync_200_ReturnsKeyedDictionary()
    {
        const string json = """
            {
              "100": {
                "name": "Site One",
                "country": "China",
                "address": { "line1": "123 Road", "townOrCity": "Shanghai" },
                "coordinates": null,
                "validFrom": "2025-01-01"
              }
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOverseasSiteAsync("org-1", "reg-1", "acc-1");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainKey("100");
        result.Value!["100"].Name.Should().Be("Site One");
        result.Value["100"].Address!.TownOrCity.Should().Be("Shanghai");
    }

    // ── Error status code mapping ─────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReExErrorKind.AuthError)]
    [InlineData(HttpStatusCode.Forbidden, ReExErrorKind.AuthError)]
    [InlineData(HttpStatusCode.NotFound, ReExErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, ReExErrorKind.ClientError)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ReExErrorKind.ClientError)]
    [InlineData(HttpStatusCode.InternalServerError, ReExErrorKind.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, ReExErrorKind.ServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ReExErrorKind.ServerError)]
    public async Task GetOrganisationsAsync_ErrorStatusCode_ReturnsCorrectErrorKind(
        HttpStatusCode statusCode, ReExErrorKind expectedKind)
    {
        var sut = BuildSut(new RawStringHandler(statusCode, "error"));

        var result = await sut.GetOrganisationsAsync("1234");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(expectedKind);
        result.StatusCode.Should().Be((int)statusCode);
        result.Value.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ReExErrorKind.AuthError)]
    [InlineData(HttpStatusCode.NotFound, ReExErrorKind.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, ReExErrorKind.ServerError)]
    public async Task GetOverseasSiteAsync_ErrorStatusCode_ReturnsCorrectErrorKind(
        HttpStatusCode statusCode, ReExErrorKind expectedKind)
    {
        var sut = BuildSut(new RawStringHandler(statusCode, "error"));

        var result = await sut.GetOverseasSiteAsync("org-1", "reg-1", "acc-1");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(expectedKind);
    }

    // ── Deserialization error ─────────────────────────────────────────────────

    [Fact]
    public async Task GetOrganisationsAsync_InvalidJson_ReturnsDeserializationError()
    {
        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, "not-valid-json{{{{"));

        var result = await sut.GetOrganisationsAsync("1234");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ReExErrorKind.DeserializationError);
        result.StatusCode.Should().Be(200);
    }

    // ── Timeout and transport ─────────────────────────────────────────────────

    [Fact]
    public async Task GetOrganisationsAsync_Timeout_ReturnsTimeoutError()
    {
        var sut = BuildSut(new TimeoutHandler());

        var result = await sut.GetOrganisationsAsync("1234");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ReExErrorKind.Timeout);
    }

    [Fact]
    public async Task GetOverseasSiteAsync_Timeout_ReturnsTimeoutError()
    {
        var sut = BuildSut(new TimeoutHandler());

        var result = await sut.GetOverseasSiteAsync("org-1", "reg-1", "acc-1");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ReExErrorKind.Timeout);
    }

    [Fact]
    public async Task GetOrganisationsAsync_TransportError_ReturnsTransportError()
    {
        var sut = BuildSut(new ExceptionHandler(new HttpRequestException("connection refused")));

        var result = await sut.GetOrganisationsAsync("1234");

        result.IsSuccess.Should().BeFalse();
        result.Error!.Kind.Should().Be(ReExErrorKind.TransportError);
    }

    // ── Never throws ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrganisationsAsync_NeverThrowsForHttpErrors()
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError })
        {
            var sut = BuildSut(new RawStringHandler(status, "err"));
            var act = () => sut.GetOrganisationsAsync("x");
            await act.Should().NotThrowAsync();
        }
    }

    // ── Polymorphic registration deserialization ──────────────────────────────

    [Fact]
    public async Task GetOrganisationsAsync_ReprocessorRegistration_DeserializesCorrectly()
    {
        const string json = """
            {
              "id": "org-1",
              "schemaVersion": 3,
              "orgId": 1234,
              "registrations": [
                {
                  "id": "reg-1",
                  "wasteProcessingType": "reprocessor",
                  "accreditationId": "acc-1",
                  "site": {
                    "address": { "line1": "1 Industrial Way", "postcode": "AB1 2CD", "town": "Leeds" },
                    "gridReference": "SE123456"
                  },
                  "yearlyMetrics": [
                    { "year": 2024, "metric": "output" }
                  ],
                  "reprocessingType": "mechanical",
                  "glassRecyclingProcess": [],
                  "noticeAddress": { "line1": "1 Road", "postcode": "AB1 2CD", "town": "Leeds", "country": "England" },
                  "wasteManagementPermits": [
                    {
                      "type": "permit",
                      "permitNumber": "EPR/AB1234CD/A001",
                      "authorisedMaterials": [
                        { "material": "paper", "authorisedWeightInTonnes": 10, "timeScale": "yearly" }
                      ]
                    }
                  ]
                }
              ],
              "accreditations": []
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("1234");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Registrations.Should().HaveCount(1);
        var reg = result.Value.Registrations[0].Should().BeOfType<ReprocessorRegistrationDto>().Subject;
        reg.Site!.Address!.Line1.Should().Be("1 Industrial Way");
        reg.Site.GridReference.Should().Be("SE123456");
        reg.YearlyMetrics.Should().HaveCount(1);
        reg.YearlyMetrics[0].Year.Should().Be(2024);
        reg.NoticeAddress!.Line1.Should().Be("1 Road");
        reg.NoticeAddress.Postcode.Should().Be("AB1 2CD");
        reg.WasteManagementPermits[0].PermitNumber.Should().Be("EPR/AB1234CD/A001");
        reg.WasteManagementPermits[0].AuthorisedMaterials[0].Material.Should().Be("paper");
    }

    [Fact]
    public async Task GetOrganisationsAsync_ExporterRegistration_DeserializesCorrectly()
    {
        const string json = """
            {
              "id": "org-1",
              "schemaVersion": 3,
              "registrations": [
                {
                  "id": "reg-2",
                  "wasteProcessingType": "exporter",
                  "exportPorts": ["Dover", "Felixstowe"],
                  "overseasSites": { "100": { "overseasSiteId": "6a2fcd76-site" } },
                  "orsFileUploads": [{ "defraFormUploadedFileId": "file-1", "defraFormUserDownloadLink": "https://example.test/file-1" }],
                  "noticeAddress": { "fullAddress": "1 Road, Shanghai, China", "country": "China" }
                }
              ],
              "accreditations": []
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("1234");

        result.IsSuccess.Should().BeTrue();
        var reg = result.Value!.Registrations[0].Should().BeOfType<ExporterRegistrationDto>().Subject;
        reg.ExportPorts.Should().BeEquivalentTo(["Dover", "Felixstowe"]);
        reg.OverseasSites["100"].OverseasSiteId.Should().Be("6a2fcd76-site");
        reg.OrsFileUploads[0].DefraFormUploadedFileId.Should().Be("file-1");
        reg.NoticeAddress!.FullAddress.Should().Be("1 Road, Shanghai, China");
        reg.NoticeAddress.Country.Should().Be("China");
    }

    [Fact]
    public async Task GetOrganisationsAsync_BothNoticeAddressShapes_RoundTripCorrectly()
    {
        const string json = """
            {
              "id": "org-1",
              "registrations": [
                {
                  "wasteProcessingType": "reprocessor",
                  "noticeAddress": { "line1": "1 Street", "postcode": "LS1 1AA", "town": "Leeds", "country": "England" }
                },
                {
                  "wasteProcessingType": "exporter",
                  "noticeAddress": { "fullAddress": "88 Nanjing Rd, Shanghai", "country": "China" }
                }
              ],
              "accreditations": []
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("org-1");

        result.IsSuccess.Should().BeTrue();
        var reprocessor = (ReprocessorRegistrationDto)result.Value!.Registrations[0];
        reprocessor.NoticeAddress!.Line1.Should().Be("1 Street");
        reprocessor.NoticeAddress.Postcode.Should().Be("LS1 1AA");
        reprocessor.NoticeAddress.FullAddress.Should().BeNull();

        var exporter = (ExporterRegistrationDto)result.Value.Registrations[1];
        exporter.NoticeAddress!.FullAddress.Should().Be("88 Nanjing Rd, Shanghai");
        exporter.NoticeAddress.Country.Should().Be("China");
        exporter.NoticeAddress.Line1.Should().BeNull();
    }

    [Fact]
    public async Task GetOrganisationsAsync_ExemptionPermit_DeserializesCorrectly()
    {
        const string json = """
            {
              "id": "org-1",
              "registrations": [
                {
                  "wasteProcessingType": "reprocessor",
                  "wasteManagementPermits": [
                    { "type": "exemption", "exemptions": [{ "reference": "T11", "exemptionCode": "T11" }] }
                  ]
                }
              ],
              "accreditations": []
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("org-1");

        result.IsSuccess.Should().BeTrue();
        var reg = (ReprocessorRegistrationDto)result.Value!.Registrations[0];
        reg.WasteManagementPermits[0].Type.Should().Be("exemption");
        reg.WasteManagementPermits[0].Exemptions[0].Reference.Should().Be("T11");
    }

    [Fact]
    public async Task GetOrganisationsAsync_RegisteredOnlyRegistration_NoAccreditationId()
    {
        const string json = """
            {
              "id": "org-1",
              "registrations": [
                { "wasteProcessingType": "reprocessor" }
              ],
              "accreditations": []
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("org-1");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Registrations[0].AccreditationId.Should().BeNull();
    }

    [Fact]
    public async Task GetOrganisationsAsync_AccreditationWithPrnIssuance_DeserializesCorrectly()
    {
        const string json = """
            {
              "id": "org-1",
              "registrations": [],
              "accreditations": [
                {
                  "id": "acc-1",
                  "accreditationNumber": "ACC/2024/001",
                  "status": "approved",
                  "prnIssuance": {
                    "tonnageBand": "upTo1000",
                    "signatories": [{ "fullName": "Jane Smith", "email": "j.smith@example.com" }],
                    "incomeBusinessPlan": [{ "description": "Infrastructure", "percentSpent": 20 }]
                  }
                }
              ]
            }
            """;

        var sut = BuildSut(new RawStringHandler(HttpStatusCode.OK, json));

        var result = await sut.GetOrganisationsAsync("org-1");

        result.IsSuccess.Should().BeTrue();
        var acc = result.Value!.Accreditations[0];
        acc.AccreditationNumber.Should().Be("ACC/2024/001");
        acc.Status.Should().Be("approved");
        acc.PrnIssuance!.TonnageBand.Should().Be("upTo1000");
        acc.PrnIssuance.Signatories[0].FullName.Should().Be("Jane Smith");
        acc.PrnIssuance.IncomeBusinessPlan[0].PercentSpent.Should().Be(20);
    }

    // ── Stub message handlers ─────────────────────────────────────────────────

    private sealed class RawStringHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RawStringHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Simulate HttpClient timeout: throws TaskCanceledException with an internal token (not the caller's)
            var cts = new CancellationTokenSource();
            cts.Cancel();
            throw new TaskCanceledException("Timed out", null, cts.Token);
        }
    }

    private sealed class ExceptionHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ExceptionHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw _exception;
    }
}
