using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

public class HttpCaseWorkingApiAdapterTests
{
    private const string TestUrl = "http://mgmt-be:8085";
    private const string TestClientId = "epr-register-enrol-backend";
    private const string TestApplicationReference = "APP26EA123451AAPL";

    private static AccreditationApplicationModel CreateTestApplication()
    {
        return new AccreditationApplicationModel
        {
            OrganisationId = "12345",
            OrgId = 500500,
            OrganisationName = "Acme Recycling Ltd",
            Year = 2026,
            RegistrationId = "reg-001",
            RegistrationReference = "EPR-100023",
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = ApplicationStatus.Started,
            SiteAddress = "123 High Street, London, SW1A 1AA",
            CompanyRegisterAddressPostcode = "EC1A 1BB",
            CompanyRegisteredAddress = "1 Acme House, London, EC1A 1BB",
            CompaniesHouseNumber = "01234567",
            PermitNumbers = ["WML123456", "PPC456789"],
            WasteProcessingType = "reprocessor",
            SubmittedBy = new SubmittedByModel
            {
                FullName = "Jane Smith",
                JobTitle = "Operations Manager",
                Email = "jane@example.com",
            },
            SubmitterContactDetails = new SubmitterContactDetailsModel
            {
                FullName = "Barton Deckow",
                Email = "barton.deckow@example.com",
                Phone = "0111 478 4919",
                JobTitle = "Human Infrastructure Architect",
            },
        };
    }

    private static (
        HttpCaseWorkingApiAdapter adapter,
        CapturingHttpMessageHandler handler
    ) CreateAdapter(
        string? url = TestUrl,
        string clientId = TestClientId,
        string? sharedSecret = null
    )
    {
        var config = Options.Create(
            new CaseWorkingApiConfig
            {
                Url = url ?? "",
                ClientId = clientId,
                SharedSecret = sharedSecret,
            }
        );

        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = Guid.NewGuid(),
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
                applicationReference = TestApplicationReference,
            }
        );

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        return (adapter, handler);
    }

    [Fact]
    public async Task SubmitApplicationAsync_Success_ReturnsApplicationReferenceFromManagementBeResponse()
    {
        var (adapter, _) = CreateAdapter();
        var result = await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );
        result.ApplicationReference.Should().Be(TestApplicationReference);
    }

    [Fact]
    public async Task SubmitApplicationAsync_Success_ReturnsWorkItemIdFromResponse()
    {
        var expectedId = Guid.NewGuid();
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = expectedId,
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
                applicationReference = TestApplicationReference,
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var result = await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        result.WorkItemId.Should().Be(expectedId);
    }

    [Fact]
    public async Task SubmitApplicationAsync_UnparseableResponseBody_ThrowsHttpRequestException()
    {
        // RA-318: applicationReference is ManagementBe-generated with no local fallback, so
        // an unparseable response can no longer be tolerated the way a missing id can — the
        // submission must fail rather than proceed without a valid reference to persist.
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new RawBodyHttpMessageHandler(HttpStatusCode.Created, "not valid json");
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SubmitApplicationAsync_NonSuccessResponse_LogsBodyAsScopedPropertyNotInMessage()
    {
        // RA-519 follow-up (log-body-message-leak): ManagementBe's error response can echo
        // submitted applicant data back verbatim (e.g. field-level validation errors), so it
        // must be attached as a scoped property under a dotted key CDP's OpenSearch allow-list
        // will drop, never interpolated into the rendered message — `message` is unconditionally
        // indexed by CDP regardless of content, so interpolating it there would defeat the
        // allow-list entirely.
        const string sensitiveResponseBody =
            """{"error":"Validation failed for jane@example.com, DOB 1990-01-01"}""";
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new RawBodyHttpMessageHandler(
            HttpStatusCode.BadRequest,
            sensitiveResponseBody
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var logger = new CapturingLogger<HttpCaseWorkingApiAdapter>();
        var adapter = new HttpCaseWorkingApiAdapter(httpClientFactory, config, logger);

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());

        await act.Should().ThrowAsync<HttpRequestException>();

        var entry = logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Which;
        entry.Message.Should().NotContain("jane@example.com");
        entry.Message.Should().NotContain("Validation failed");
        entry
            .ScopeProperties.Should()
            .ContainKey("http.response.body")
            .WhoseValue.Should()
            .Be(sensitiveResponseBody);
    }

    [Fact]
    public async Task SubmitApplicationAsync_ClientTimeout_ThrowsCaseWorkingApiTimeoutException()
    {
        // Simulates the "DefaultClient" HttpClient.Timeout (Program.cs, 15s) elapsing: HttpClient
        // surfaces this as a TaskCanceledException that is NOT attributable to the caller's own
        // cancellationToken (which stays uncancelled here). RA-311 fix 3 requires this to become
        // a clean, distinguishable error rather than propagating an unhandled/generic exception.
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new TimeoutHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () =>
            adapter.SubmitApplicationAsync(CreateTestApplication(), CancellationToken.None);

        await act.Should().ThrowAsync<CaseWorkingApiTimeoutException>();
    }

    [Fact]
    public async Task SubmitApplicationAsync_CallerCancellation_DoesNotThrowTimeoutException()
    {
        // Distinguishes "the client's own Timeout elapsed" from "the caller cancelled us" — only
        // the former should be reported as CaseWorkingApiTimeoutException. Both surface as
        // TaskCanceledException from HttpClient, so the adapter must tell them apart via the
        // supplied CancellationToken's own IsCancellationRequested state.
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new TimeoutHttpMessageHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication(), cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public async Task SubmitApplicationAsync_ResponseMissingApplicationReference_ThrowsHttpRequestException()
    {
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                id = Guid.NewGuid(),
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .WithMessage("*application reference*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_ResponseMissingIdField_ReturnsNullWorkItemId()
    {
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.Created,
            new
            {
                typeId = "re-accreditation",
                stateId = "submitted",
                payload = new { },
                applicationReference = TestApplicationReference,
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var result = await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        result.ApplicationReference.Should().Be(TestApplicationReference);
        result.WorkItemId.Should().BeNull();
    }

    [Fact]
    public async Task SubmitApplicationAsync_MapsPayloadCorrectly()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        handler.CapturedRequestBody.Should().NotBeNullOrEmpty();
        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;

        root.GetProperty("typeId").GetString().Should().Be("re-accreditation");
        root.GetProperty("source").GetString().Should().Be("operator-fe");

        var payload = root.GetProperty("payload");
        payload.GetProperty("organisationName").GetString().Should().Be("Acme Recycling Ltd");
        payload.GetProperty("registrationNumber").GetString().Should().Be("EPR-100023");
        payload.GetProperty("material").GetString().Should().Be("plastic");
        payload.GetProperty("previousAccreditationYear").GetInt32().Should().Be(2025);
        payload.GetProperty("complianceIssuesReported").GetInt32().Should().Be(0);
        payload.GetProperty("operatorOrganisationId").GetString().Should().Be("12345");
        payload.GetProperty("operatorOrgNumber").GetInt32().Should().Be(500500);
        payload.GetProperty("operatorRegistrationId").GetString().Should().Be("reg-001");
        payload.GetProperty("operatorEmail").GetString().Should().Be("jane@example.com");
        payload.GetProperty("siteAddressPostcode").GetString().Should().Be("SW1A 1AA");
        payload.GetProperty("companyRegisterAddressPostcode").GetString().Should().Be("EC1A 1BB");
        payload
            .GetProperty("companyRegisteredAddress")
            .GetString()
            .Should()
            .Be("1 Acme House, London, EC1A 1BB");
        payload.GetProperty("companiesHouseNumber").GetString().Should().Be("01234567");
        payload
            .GetProperty("permitNumbers")
            .EnumerateArray()
            .Select(e => e.GetString())
            .Should()
            .BeEquivalentTo(["WML123456", "PPC456789"]);
        payload.GetProperty("wasteProcessingType").GetString().Should().Be("reprocessor");

        var submitterContactDetails = payload.GetProperty("submitterContactDetails");
        submitterContactDetails.GetProperty("fullName").GetString().Should().Be("Barton Deckow");
        submitterContactDetails
            .GetProperty("email")
            .GetString()
            .Should()
            .Be("barton.deckow@example.com");
        submitterContactDetails.GetProperty("phone").GetString().Should().Be("0111 478 4919");
        submitterContactDetails
            .GetProperty("jobTitle")
            .GetString()
            .Should()
            .Be("Human Infrastructure Architect");
    }

    // RA-503: OrgId is resolved fresh from ReEx immediately before submission (see the Submit
    // endpoint) and can be null on a lookup failure - the payload must omit operatorOrgNumber
    // rather than send a fabricated value, same as every other nullable field here.
    [Fact]
    public async Task SubmitApplicationAsync_NullOrgId_OmitsOperatorOrgNumberFromPayload()
    {
        var (adapter, handler) = CreateAdapter();
        var application = CreateTestApplication();
        application.OrgId = null;

        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");
        payload.TryGetProperty("operatorOrgNumber", out _).Should().BeFalse();
    }

    // RA-480
    [Fact]
    public async Task SubmitApplicationAsync_NoSubmitterContactDetails_OmitsSubmitterContactDetailsFromPayload()
    {
        var application = CreateTestApplication();
        application.SubmitterContactDetails = null;

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var payload = JsonDocument
            .Parse(handler.CapturedRequestBody!)
            .RootElement.GetProperty("payload");
        payload.TryGetProperty("submitterContactDetails", out _).Should().BeFalse();
    }

    // RA-456
    [Fact]
    public async Task SubmitApplicationAsync_MapsBusinessPlanOtherFieldsIntoPayload()
    {
        var application = CreateTestApplication();
        application.BusinessPlan.OtherPercent = 15;
        application.BusinessPlan.OtherDetail = "Other spend detail";

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var businessPlan = doc.RootElement.GetProperty("payload").GetProperty("businessPlan");
        businessPlan.GetProperty("otherPercent").GetInt32().Should().Be(15);
        businessPlan.GetProperty("otherDetail").GetString().Should().Be("Other spend detail");
    }

    // RA-434
    [Fact]
    public async Task SubmitApplicationAsync_NoPermitNumbers_SendsEmptyPermitNumbersArray()
    {
        var application = CreateTestApplication();
        application.PermitNumbers = [];

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");

        payload.GetProperty("permitNumbers").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_GlassMaterial_IncludesGlassRecyclingProcessInPayload()
    {
        var application = CreateTestApplication();
        application.MaterialType = MaterialType.Glass;
        application.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");

        payload.GetProperty("materialsHandled")[0].GetString().Should().Be("glass");
        payload.GetProperty("glassRecyclingProcess").GetString().Should().Be("glass_re_melt");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NonGlassMaterial_OmitsGlassRecyclingProcessFromPayload()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");

        payload.TryGetProperty("glassRecyclingProcess", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitApplicationAsync_OrsSite_ForwardsOrsIdAndIsNewSite()
    {
        var application = CreateTestApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "001",
                    SiteName = "Overseas Recycling Co",
                    IsNewSite = false,
                },
            ],
        };

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var site = doc
            .RootElement.GetProperty("payload")
            .GetProperty("overseasSites")
            .GetProperty("sites")[0];

        site.GetProperty("orsId").GetString().Should().Be("001");
        site.GetProperty("isNewSite").GetBoolean().Should().BeFalse();
        site.TryGetProperty("interimSite", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitApplicationAsync_SiteWithInterimSite_ForwardsNestedInterimSiteObject()
    {
        var application = CreateTestApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "001",
                    SiteName = "Overseas Recycling Co",
                    IsNewSite = true,
                    InterimSite = new InterimSiteModel
                    {
                        SiteId = 2,
                        SiteNumber = "SN-0002",
                        Country = "France",
                        SiteName = "Interim Recycling Site",
                        AddressLine1 = "1 Rue Example",
                        TownOrCity = "Paris",
                        ContactName = "Jane Smith",
                        ContactEmail = "jane.smith@example.com",
                        ContactPhone = "+33 1 23 45 67 89",
                        // Set explicitly: IsNewSite defaults to false (RA-292), so relying on the
                        // default here would assert nothing about forwarding.
                        IsNewSite = true,
                    },
                },
            ],
        };

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var interimSite = doc
            .RootElement.GetProperty("payload")
            .GetProperty("overseasSites")
            .GetProperty("sites")[0]
            .GetProperty("interimSite");

        interimSite.GetProperty("siteNumber").GetString().Should().Be("SN-0002");
        interimSite.GetProperty("isNewSite").GetBoolean().Should().BeTrue();
        interimSite.GetProperty("siteName").GetString().Should().Be("Interim Recycling Site");
        interimSite.GetProperty("townOrCity").GetString().Should().Be("Paris");
        interimSite.GetProperty("contactPhone").GetString().Should().Be("+33 1 23 45 67 89");
    }

    #region RA-292 — ORS / interim / authority-to-issue wire contract

    // RA-292 AC01/AC02/AC04. ManagementBe can only show the regulator what we send, so the
    // serialised shape is pinned in full rather than field-by-field: a silently dropped key is
    // exactly the failure this ticket exists to fix.

    private static OverseasSiteModel FullyPopulatedSite() =>
        new()
        {
            SiteId = 1,
            OrsId = "001",
            SiteName = "Overseas Recycling Co",
            SiteAddress = "1 Rue Example, Paris, 75001",
            AddressLine1 = "1 Rue Example",
            AddressLine2 = "Zone Industrielle",
            TownOrCity = "Paris",
            Country = "France",
            Coordinates = "48.8566,2.3522",
            ContactName = "Pierre Dupont",
            ContactEmail = "pierre@example.com",
            ContactPhone = "+33 1 23 45 67 89",
            OperationCodes = ["R3"],
            Code1 = "B3011",
            Code2 = "B3020",
            Code3 = "GH013",
            RepatriatedLoads = "12",
            ConditionsOfExport = true,
            IsEu = true,
            IsOecd = true,
            IsNewSite = true,
            RegisteredNowAccredited = true,
            BesEvidence = new BesEvidenceModel
            {
                BesEvidenceUploads =
                [
                    new BesEvidenceFileModel
                    {
                        FileId = "file-1",
                        Filename = "bes.pdf",
                        ContentType = "application/pdf",
                        ScanStatus = "complete",
                        BesEvidenceValidFromDate = "2026-01-01",
                        BesEvidenceExpiryDate = "2027-01-01",
                        UploadedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                        S3Key = "key-1",
                        S3Bucket = "bucket-1",
                    },
                ],
            },
            InterimSite = new InterimSiteModel
            {
                SiteId = 2,
                SiteNumber = "SN-0002",
                Country = "France",
                SiteName = "Interim Recycling Site",
                AddressLine1 = "9 Rue Interim",
                AddressLine2 = "Batiment B",
                TownOrCity = "Lyon",
                StateOrRegion = "Auvergne",
                Postcode = "69001",
                ContactName = "Marie Curie",
                ContactEmail = "marie@example.com",
                ContactPhone = "+33 4 11 22 33 44",
                OperationCodes = ["R12"],
                IsNewSite = true,
            },
        };

    // Only the two properties the model makes mandatory. Stands in for a work item whose site
    // data predates RA-292 or was never captured — note it serialises with isNewSite false, since
    // a site with no stored newness must not arrive at the regulator wearing a "new" badge.
    private static OverseasSiteModel BareSite() => new() { SiteId = 3, SiteName = "Sparse Site" };

    // RA-483: what the operator's "remove" journey actually produces — the site stays in the
    // application record, deselected, rather than being deleted.
    private static OverseasSiteModel DeselectedSite(int siteId = 9) =>
        new()
        {
            SiteId = siteId,
            SiteName = "Removed Site",
            Country = "Germany",
            Selected = false,
        };

    private const string ExpectedOverseasSitesJson = """
        {
          "sites": [
            {
              "siteId": 1,
              "selected": true,
              "orsId": "001",
              "siteName": "Overseas Recycling Co",
              "siteAddress": "1 Rue Example, Paris, 75001",
              "addressLine1": "1 Rue Example",
              "addressLine2": "Zone Industrielle",
              "townOrCity": "Paris",
              "country": "France",
              "coordinates": "48.8566,2.3522",
              "contactName": "Pierre Dupont",
              "contactEmail": "pierre@example.com",
              "contactPhone": "+33 1 23 45 67 89",
              "operationCodes": ["R3"],
              "code1": "B3011",
              "code2": "B3020",
              "code3": "GH013",
              "repatriatedLoads": "12",
              "conditionsOfExport": true,
              "isEu": true,
              "isOecd": true,
              "isNewSite": true,
              "registeredNowAccredited": true,
              "interimSite": {
                "siteId": 2,
                "siteNumber": "SN-0002",
                "isNewSite": true,
                "country": "France",
                "siteName": "Interim Recycling Site",
                "addressLine1": "9 Rue Interim",
                "addressLine2": "Batiment B",
                "townOrCity": "Lyon",
                "stateOrRegion": "Auvergne",
                "postcode": "69001",
                "contactName": "Marie Curie",
                "contactEmail": "marie@example.com",
                "contactPhone": "+33 4 11 22 33 44",
                "operationCodes": ["R12"]
              },
              "besEvidence": {
                "files": [
                  {
                    "fileId": "file-1",
                    "filename": "bes.pdf",
                    "contentType": "application/pdf",
                    "uploadedAt": "2026-01-02T03:04:05Z",
                    "scanStatus": "complete",
                    "besEvidenceValidFromDate": "2026-01-01",
                    "besEvidenceExpiryDate": "2027-01-01",
                    "s3Key": "key-1",
                    "s3Bucket": "bucket-1"
                  }
                ]
              }
            },
            {
              "siteId": 3,
              "selected": true,
              "siteName": "Sparse Site",
              "operationCodes": [],
              "isEu": false,
              "isOecd": false,
              "isNewSite": false,
              "registeredNowAccredited": false,
              "besEvidence": {
                "files": []
              }
            }
          ]
        }
        """;

    private const string ExpectedPrnsJson = """
        {
          "plannedTonnageBand": "UpTo5000",
          "authorisers": [
            {
              "fullName": "Old Hand",
              "email": "old@example.com",
              "isNew": false
            },
            {
              "fullName": "Fresh Face",
              "email": "fresh@example.com",
              "isNew": true
            }
          ]
        }
        """;

    // Compares field names, field order and values exactly, while letting the expected literal
    // above stay readable. JsonNode preserves object member order on both sides.
    private static string Canonical(string json) => JsonNode.Parse(json)!.ToJsonString();

    private static AccreditationApplicationModel ApplicationWithRa292Data()
    {
        var application = CreateTestApplication();
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo5000;
        application.Prns.Authorisers =
        [
            new PrnsAuthoriser
            {
                FullName = "Old Hand",
                Email = "old@example.com",
                IsNew = false,
            },
            new PrnsAuthoriser
            {
                FullName = "Fresh Face",
                Email = "fresh@example.com",
                IsNew = true,
            },
        ];
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [FullyPopulatedSite(), BareSite()],
        };
        return application;
    }

    private static async Task<JsonElement> CapturedSubmitPayload(
        AccreditationApplicationModel application
    )
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application);
        return JsonDocument.Parse(handler.CapturedRequestBody!).RootElement.GetProperty("payload");
    }

    private static async Task<JsonElement> CapturedResumeSections(
        AccreditationApplicationModel application,
        params string[] sectionKeys
    )
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            application,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            sectionKeys
        );
        return JsonDocument.Parse(handler.CapturedRequestBody!).RootElement.GetProperty("sections");
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmitsFullOverseasSitesContract()
    {
        var payload = await CapturedSubmitPayload(ApplicationWithRa292Data());

        Canonical(payload.GetProperty("overseasSites").GetRawText())
            .Should()
            .Be(Canonical(ExpectedOverseasSitesJson));
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmitsAuthorisersWithIsNewFlag()
    {
        var payload = await CapturedSubmitPayload(ApplicationWithRa292Data());

        Canonical(payload.GetProperty("prns").GetRawText())
            .Should()
            .Be(Canonical(ExpectedPrnsJson));
    }

    [Fact]
    public async Task ResumeFromQueryAsync_OverseasSitesSection_EmitsFullOverseasSitesContract()
    {
        // RA-292 (D): this projection used to be a weaker copy of the submit one — no orsId, no
        // isNewSite, no interimSite at all — so resubmitting a queried ORS section wiped the
        // interim site data ManagementBe held.
        var sections = await CapturedResumeSections(
            ApplicationWithRa292Data(),
            "overseas-reprocessing-sites"
        );

        Canonical(sections.GetProperty("OverseasSites").GetRawText())
            .Should()
            .Be(Canonical(ExpectedOverseasSitesJson));
    }

    [Fact]
    public async Task ResumeFromQueryAsync_PrnsSection_EmitsAuthorisersWithIsNewFlag()
    {
        var sections = await CapturedResumeSections(
            ApplicationWithRa292Data(),
            "authority-to-issue"
        );

        Canonical(sections.GetProperty("Prns").GetRawText())
            .Should()
            .Be(Canonical(ExpectedPrnsJson));
    }

    [Theory]
    [InlineData("overseas-reprocessing-sites", "OverseasSites", "overseasSites")]
    [InlineData("authority-to-issue", "Prns", "prns")]
    [InlineData("prn-tonnage", "Prns", "prns")]
    public async Task ResumeFromQueryAsync_SectionMatchesSubmitPayloadByteForByte(
        string sectionKey,
        string sectionName,
        string payloadName
    )
    {
        // Belt and braces on top of the literals above: whatever the submit payload says, the
        // resubmit payload must say the same, so the two can never drift apart again.
        var application = ApplicationWithRa292Data();

        var payload = await CapturedSubmitPayload(application);
        var sections = await CapturedResumeSections(application, sectionKey);

        Canonical(sections.GetProperty(sectionName).GetRawText())
            .Should()
            .Be(Canonical(payload.GetProperty(payloadName).GetRawText()));
    }

    [Fact]
    public async Task SubmitApplicationAsync_SiteWithNoOptionalValues_OmitsThoseKeysEntirely()
    {
        // RA-292 (E): absent must stay safe for ManagementBe. Nulls are dropped rather than sent
        // as explicit nulls, and no new field is ever mandatory on the consumer side.
        var application = CreateTestApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [BareSite()],
        };

        var payload = await CapturedSubmitPayload(application);
        var site = payload.GetProperty("overseasSites").GetProperty("sites")[0];

        foreach (
            var absentKey in new[]
            {
                "orsId",
                "siteAddress",
                "addressLine1",
                "addressLine2",
                "townOrCity",
                "country",
                "coordinates",
                "contactName",
                "contactEmail",
                "contactPhone",
                "code1",
                "code2",
                "code3",
                "repatriatedLoads",
                "conditionsOfExport",
                "interimSite",
            }
        )
        {
            site.TryGetProperty(absentKey, out _)
                .Should()
                .BeFalse($"'{absentKey}' is null and must be omitted, not sent as null");
        }

        // OperationCodes is a list, not a nullable scalar — an unset value serialises as an
        // empty array rather than being dropped by JsonOptions' WhenWritingNull.
        site.GetProperty("operationCodes").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoOverseasSites_SendsEmptySiteArray()
    {
        var payload = await CapturedSubmitPayload(CreateTestApplication());

        payload
            .GetProperty("overseasSites")
            .GetProperty("sites")
            .EnumerateArray()
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoAuthorisers_SendsEmptyAuthoriserArray()
    {
        var payload = await CapturedSubmitPayload(CreateTestApplication());

        payload.GetProperty("prns").GetProperty("authorisers").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_NoOverseasSites_SendsEmptySiteArray()
    {
        var application = CreateTestApplication();
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.OverseasSites = null;

        var sections = await CapturedResumeSections(application, "overseas-reprocessing-sites");

        sections
            .GetProperty("OverseasSites")
            .GetProperty("sites")
            .EnumerateArray()
            .Should()
            .BeEmpty();
    }

    #endregion

    #region RA-483 removed (deselected) overseas sites

    private static AccreditationApplicationModel ApplicationWithMixedSelection()
    {
        var application = CreateTestApplication();
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [FullyPopulatedSite(), DeselectedSite(), BareSite()],
        };
        return application;
    }

    private static AccreditationApplicationModel ApplicationWithAllSitesDeselected()
    {
        var application = CreateTestApplication();
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [DeselectedSite(9), DeselectedSite(10)],
        };
        return application;
    }

    private static IEnumerable<int> SiteIds(JsonElement section) =>
        section
            .GetProperty("sites")
            .EnumerateArray()
            .Select(s => s.GetProperty("siteId").GetInt32());

    [Fact]
    public async Task SubmitApplicationAsync_DeselectedSite_IsExcludedFromPayload()
    {
        // RA-483 AC01: a removed ORS must not reach the regulator's work-item screen at all.
        var payload = await CapturedSubmitPayload(ApplicationWithMixedSelection());

        SiteIds(payload.GetProperty("overseasSites")).Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_DeselectedSite_IsExcludedFromSectionPayload()
    {
        // The resubmit-after-query projection must filter identically, or a removed site would
        // reappear on the work item the moment the operator answered a query.
        var sections = await CapturedResumeSections(
            ApplicationWithMixedSelection(),
            "overseas-reprocessing-sites"
        );

        SiteIds(sections.GetProperty("OverseasSites")).Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public async Task SubmitApplicationAsync_AllSitesDeselected_SendsEmptySiteArray()
    {
        // Guard on the filter's edge case: an empty array, never null and never a missing key,
        // so ManagementBe keeps parsing the section the same way.
        var payload = await CapturedSubmitPayload(ApplicationWithAllSitesDeselected());

        var sites = payload.GetProperty("overseasSites").GetProperty("sites");
        sites.ValueKind.Should().Be(JsonValueKind.Array);
        sites.EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_AllSitesDeselected_SendsEmptySiteArray()
    {
        var sections = await CapturedResumeSections(
            ApplicationWithAllSitesDeselected(),
            "overseas-reprocessing-sites"
        );

        var sites = sections.GetProperty("OverseasSites").GetProperty("sites");
        sites.ValueKind.Should().Be(JsonValueKind.Array);
        sites.EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmitsSelectedTrueOnEverySurvivingSite()
    {
        // Cross-repo contract: `selected` is a JSON boolean on each element of
        // payload.overseasSites.sites — absent or true means visible, explicit false means
        // removed. Emitting it lets management-fe filter defensively on its own.
        var payload = await CapturedSubmitPayload(ApplicationWithMixedSelection());

        foreach (
            var site in payload.GetProperty("overseasSites").GetProperty("sites").EnumerateArray()
        )
        {
            site.TryGetProperty("selected", out var selected)
                .Should()
                .BeTrue("every projected site must carry the 'selected' flag");
            selected.ValueKind.Should().Be(JsonValueKind.True);
        }
    }

    [Fact]
    public async Task ResumeFromQueryAsync_EmitsSelectedTrueOnEverySurvivingSite()
    {
        var sections = await CapturedResumeSections(
            ApplicationWithMixedSelection(),
            "overseas-reprocessing-sites"
        );

        foreach (
            var site in sections.GetProperty("OverseasSites").GetProperty("sites").EnumerateArray()
        )
        {
            site.TryGetProperty("selected", out var selected)
                .Should()
                .BeTrue("every projected site must carry the 'selected' flag");
            selected.ValueKind.Should().Be(JsonValueKind.True);
        }
    }

    [Fact]
    public async Task ResumeFromQueryAsync_MixedSelection_MatchesSubmitPayloadByteForByte()
    {
        // The two projections share one helper; this pins that they stay identical under the
        // RA-483 filter too, so a resubmit can never restore what the submit dropped.
        var application = ApplicationWithMixedSelection();

        var payload = await CapturedSubmitPayload(application);
        var sections = await CapturedResumeSections(application, "overseas-reprocessing-sites");

        Canonical(sections.GetProperty("OverseasSites").GetRawText())
            .Should()
            .Be(Canonical(payload.GetProperty("overseasSites").GetRawText()));
    }

    #endregion

    [Fact]
    public async Task SubmitApplicationAsync_SetsAuthHeaders()
    {
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        handler.CapturedRequest.Should().NotBeNull();
        var request = handler.CapturedRequest!;
        request.Headers.GetValues("x-cdp-client-id").Should().ContainSingle(TestClientId);
        request.Headers.GetValues("x-cdp-user-id").Should().ContainSingle("jane@example.com");
        request.Headers.GetValues("x-cdp-user-name").Should().ContainSingle("Jane Smith");
    }

    // Covers the `userId ?? OrganisationId` fallback and BuildRequest's
    // `!string.IsNullOrEmpty(userName)` false branch — every other test supplies SubmittedBy.
    [Fact]
    public async Task SubmitApplicationAsync_NoSubmittedBy_UsesOrganisationIdAsUserIdAndOmitsUserNameHeader()
    {
        var application = CreateTestApplication();
        application.SubmittedBy = null;

        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(application, TestContext.Current.CancellationToken);

        var request = handler.CapturedRequest!;
        request.Headers.GetValues("x-cdp-user-id").Should().ContainSingle("12345");
        request.Headers.Contains("x-cdp-user-name").Should().BeFalse();
    }

    // Covers the generic `catch (Exception ex) { ...; throw; }` branch — distinct from the
    // TaskCanceledException-specific catches already exercised by the timeout/cancellation tests.
    [Fact]
    public async Task SubmitApplicationAsync_NetworkFailure_RethrowsException()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());

        await act.Should()
            .ThrowAsync<HttpRequestException>()
            .WithMessage("*Simulated network failure*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithSharedSecret_SetsHmacHeaders()
    {
        var (adapter, handler) = CreateAdapter(sharedSecret: "test-secret-key");
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        var request = handler.CapturedRequest!;
        request.Headers.Contains("x-cdp-auth-signature").Should().BeTrue();
        request.Headers.Contains("x-cdp-auth-timestamp").Should().BeTrue();
        request.Headers.Contains("x-cdp-auth-nonce").Should().BeTrue();

        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        DateTime.TryParse(timestamp, out _).Should().BeTrue("timestamp should be parseable");

        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        nonce.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithSharedSecret_SignatureIsValid()
    {
        const string secret = "test-secret-key";
        var (adapter, handler) = CreateAdapter(sharedSecret: secret);
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        var request = handler.CapturedRequest!;
        var timestamp = request.Headers.GetValues("x-cdp-auth-timestamp").Single();
        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();
        var actualSignature = request.Headers.GetValues("x-cdp-auth-signature").Single();

        var expectedSignature = HttpCaseWorkingApiAdapter.ComputeSignature(
            secret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            timestamp,
            nonce
        );

        actualSignature.Should().Be(expectedSignature);
    }

    [Fact]
    public async Task SubmitApplicationAsync_WithoutSharedSecret_DoesNotSetHmacHeaders()
    {
        var (adapter, handler) = CreateAdapter(sharedSecret: null);
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        var request = handler.CapturedRequest!;
        request.Headers.Contains("x-cdp-auth-signature").Should().BeFalse();
        request.Headers.Contains("x-cdp-auth-timestamp").Should().BeFalse();
        request.Headers.Contains("x-cdp-auth-nonce").Should().BeFalse();
        request.Headers.Contains("x-cdp-client-id").Should().BeTrue();
    }

    [Fact]
    public async Task SubmitApplicationAsync_NonSuccessResponse_ThrowsHttpRequestException()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error", detail = "Something broke" }
        );

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));

        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*500*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_DoesNotSendApplicationReferenceInRequest()
    {
        // RA-318: ManagementBe owns reference generation and ignores any value a caller
        // sends, so the backend must not send one at all — sending a value it knows will be
        // silently discarded is misleading about where the reference actually comes from.
        var (adapter, handler) = CreateAdapter();
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.TryGetProperty("applicationReference", out _).Should().BeFalse();
    }

    [Fact]
    public async Task SubmitApplicationAsync_EmptyUrl_ThrowsInvalidOperationException()
    {
        var (adapter, _) = CreateAdapter(url: "");

        var act = () => adapter.SubmitApplicationAsync(CreateTestApplication());
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not configured*");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NullSiteAddress_SendsNullPostcode()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.SiteAddress = null;

        await adapter.SubmitApplicationAsync(app, TestContext.Current.CancellationToken);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var payload = doc.RootElement.GetProperty("payload");
        payload
            .TryGetProperty("siteAddressPostcode", out var postcodeEl)
            .Should()
            .BeFalse("null-valued properties are excluded by WhenWritingNull");
    }

    [Fact]
    public async Task SubmitApplicationAsync_PostsToCorrectUrl()
    {
        var (adapter, handler) = CreateAdapter(url: "http://my-mgmt-be:9090");
        await adapter.SubmitApplicationAsync(
            CreateTestApplication(),
            TestContext.Current.CancellationToken
        );

        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be("http://my-mgmt-be:9090/work-items");
    }

    [Fact]
    public void ComputeSignature_MatchesKnownValue()
    {
        var result = HttpCaseWorkingApiAdapter.ComputeSignature(
            "my-secret",
            "my-client",
            null,
            null,
            "2026-06-22T10:00:00Z",
            "dGVzdC1ub25jZQ=="
        );

        // Pre-computed: HMAC-SHA256("my-secret", "v3\nmy-client\n\n\n2026-06-22T10:00:00Z\ndGVzdC1ub25jZQ==")
        // Computed with: printf 'v3\n...' | openssl dgst -sha256 -hmac 'my-secret' -binary | base64
        // Pinning the exact value (not just base64-of-32-bytes) is what
        // actually guards this port against drifting from ManagementBe's
        // canonical payload format — see the class comment on ComputeSignature.
        result.Should().Be("jjnCJCHFRVd/zdy16hBAQIzJ1NqP4OlupV4vlLvj9V4=");
    }

    [Theory]
    [InlineData("123 High Street, London, SW1A 1AA", "SW1A 1AA")]
    [InlineData("Flat 2, 10 Park Road, Manchester, M1 4BT", "M1 4BT")]
    [InlineData("SW1A 1AA", "SW1A 1AA")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ExtractPostcode_ReturnsExpected(string? input, string? expected)
    {
        HttpCaseWorkingApiAdapter.ExtractPostcode(input).Should().Be(expected);
    }

    // --- GetNotificationStatusAsync ---

    [Fact]
    public async Task GetNotificationStatusAsync_NoLinkedWorkItem_ReturnsNullWithoutCallingManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        var result = await adapter.GetNotificationStatusAsync(
            app,
            TestContext.Current.CancellationToken
        );

        result.NotificationStatus.Should().BeNull();
        result.SlaDueDate.Should().BeNull();
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationStatusAsync_ResolvesFromAuditLog()
    {
        var workItemId = Guid.NewGuid();
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            new
            {
                auditLog = new[]
                {
                    new
                    {
                        action = "notification-sent",
                        details = new Dictionary<string, string?>
                        {
                            ["templateKey"] = "SubmissionConfirmation",
                        },
                        createdAt = DateTime.UtcNow,
                    },
                },
            }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        var result = await adapter.GetNotificationStatusAsync(
            app,
            TestContext.Current.CancellationToken
        );

        result.NotificationStatus.Should().Be("sent");
        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/{workItemId}");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task GetNotificationStatusAsync_ParsesSlaDueDate()
    {
        var workItemId = Guid.NewGuid();
        var slaDueDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var config = Options.Create(
            new CaseWorkingApiConfig { Url = TestUrl, ClientId = TestClientId }
        );
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.OK,
            new { auditLog = Array.Empty<object>(), slaDueDate }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        var result = await adapter.GetNotificationStatusAsync(
            app,
            TestContext.Current.CancellationToken
        );

        result.SlaDueDate.Should().Be(slaDueDate);
    }

    [Fact]
    public async Task GetNotificationStatusAsync_NonSuccessResponse_ReturnsNull()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.GetNotificationStatusAsync(
            app,
            TestContext.Current.CancellationToken
        );

        result.NotificationStatus.Should().BeNull();
        result.SlaDueDate.Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationStatusAsync_ManagementBeUnreachable_ReturnsNull()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.GetNotificationStatusAsync(
            app,
            TestContext.Current.CancellationToken
        );

        result.NotificationStatus.Should().BeNull();
        result.SlaDueDate.Should().BeNull();
    }

    [Fact]
    public async Task GetNotificationStatusAsync_EmptyUrl_ReturnsNullWithoutThrowing()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.GetNotificationStatusAsync(
            app,
            TestContext.Current.CancellationToken
        );

        result.NotificationStatus.Should().BeNull();
        result.SlaDueDate.Should().BeNull();
    }

    // Covers the `userId ?? OrganisationId` fallback and BuildRequest's
    // `!string.IsNullOrEmpty(userName)` false branch for this method's own request-building code.
    [Fact]
    public async Task GetNotificationStatusAsync_NoSubmittedBy_UsesOrganisationIdAsUserId()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.SubmittedBy = null;
        app.CaseManagementWorkItemId = Guid.NewGuid();

        await adapter.GetNotificationStatusAsync(app, TestContext.Current.CancellationToken);

        var request = handler.CapturedRequest!;
        request.Headers.GetValues("x-cdp-user-id").Should().ContainSingle("12345");
        request.Headers.Contains("x-cdp-user-name").Should().BeFalse();
    }

    // Covers `detail?.AuditLog` / `detail?.SlaDueDate` when ManagementBe's response body
    // deserializes to a null WorkItemDetailResponseDto (e.g. a literal "null" JSON body) rather
    // than throwing — must still resolve to an empty status, not propagate a NullReferenceException.
    [Fact]
    public async Task GetNotificationStatusAsync_NullResponseBody_ReturnsNullWithoutThrowing()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new RawBodyHttpMessageHandler(HttpStatusCode.OK, "null");
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.GetNotificationStatusAsync(
            app,
            TestContext.Current.CancellationToken
        );

        result.NotificationStatus.Should().BeNull();
        result.SlaDueDate.Should().BeNull();
    }

    // --- ResumeFromQueryAsync ---

    [Fact]
    public async Task ResumeFromQueryAsync_NoLinkedWorkItem_ReturnsFailureWithoutCallingManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_EmptyUrl_ReturnsFailureWithoutThrowing()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_Success_PostsToWorkItemResumeFromQueryUrl()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue();
        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/re-accreditation/{workItemId}/resume-from-query");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_MapsResponderContactDetailsAndSectionKeysIntoPayload()
    {
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();
        app.BusinessPlan.NewInfrastructurePercent = 40;

        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;

        root.GetProperty("responderContactDetails")
            .GetProperty("fullName")
            .GetString()
            .Should()
            .Be("Jane Smith");
        root.GetProperty("responderContactDetails")
            .GetProperty("email")
            .GetString()
            .Should()
            .Be("jane@example.com");
        root.GetProperty("sectionKeys")[0].GetString().Should().Be("business-plan");
        root.GetProperty("sections")
            .GetProperty("BusinessPlan")
            .GetProperty("newInfrastructurePercent")
            .GetInt32()
            .Should()
            .Be(40);
    }

    // RA-456
    [Fact]
    public async Task ResumeFromQueryAsync_MapsBusinessPlanOtherFieldsIntoPayload()
    {
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();
        app.BusinessPlan.OtherPercent = 15;
        app.BusinessPlan.OtherDetail = "Other spend detail";

        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            TestContext.Current.CancellationToken
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var businessPlan = doc.RootElement.GetProperty("sections").GetProperty("BusinessPlan");
        businessPlan.GetProperty("otherPercent").GetInt32().Should().Be(15);
        businessPlan.GetProperty("otherDetail").GetString().Should().Be("Other spend detail");
    }

    [Fact]
    public async Task ResumeFromQueryAsync_DoesNotSendContactDetailsPropertyName()
    {
        // OBE-F5: MBE-1 expects "responderContactDetails", not "contactDetails" — assert the
        // old property name is genuinely absent rather than substring-matching the body.
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.TryGetProperty("contactDetails", out _).Should().BeFalse();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_NonSuccessResponse_ReturnsFailure()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_ManagementBeUnreachable_ReturnsFailureWithoutThrowing()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.ResumeFromQueryAsync(
            app,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
    }

    // --- WithdrawApplicationAsync ---

    // The acting user withdrawing now — deliberately different from CreateTestApplication's
    // SubmittedBy (Jane Smith) so tests can prove the adapter sends the withdrawer's identity,
    // not the original submitter's (RA-252 review fix).
    private static QuerySubmitterContactDetails WithdrawingUserContactDetails() =>
        new()
        {
            FullName = "Alex Withdrawer",
            Email = "alex.withdrawer@example.com",
            Role = string.Empty,
        };

    [Fact]
    public async Task WithdrawApplicationAsync_NoLinkedWorkItem_ReturnsFailureWithoutCallingManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_EmptyUrl_ReturnsFailureWithoutThrowing()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_Success_PostsToWorkItemWithdrawUrl()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue();
        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/re-accreditation/{workItemId}/withdraw");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task WithdrawApplicationAsync_MapsReasonIntoPayload()
    {
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var (adapter, handler) = CreateAdapter();
        await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        doc.RootElement.GetProperty("reason").GetString().Should().Be("No longer required");
    }

    [Fact]
    public async Task WithdrawApplicationAsync_SetsAuthHeadersFromActingUserNotOriginalSubmitter()
    {
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var (adapter, handler) = CreateAdapter();
        await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        handler.CapturedRequest.Should().NotBeNull();
        var request = handler.CapturedRequest!;
        request
            .Headers.GetValues("x-cdp-user-id")
            .Should()
            .ContainSingle("alex.withdrawer@example.com");
        request.Headers.GetValues("x-cdp-user-name").Should().ContainSingle("Alex Withdrawer");
    }

    [Fact]
    public async Task WithdrawApplicationAsync_NonSuccessResponse_ReturnsFailure()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_ManagementBeUnreachable_ReturnsFailureWithoutThrowing()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var result = await adapter.WithdrawApplicationAsync(
            app,
            WithdrawingUserContactDetails(),
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeFalse();
    }

    // --- NotifySiteAddedAsync ---

    [Fact]
    public async Task NotifySiteAddedAsync_NoLinkedWorkItem_DoesNotCallManagementBe()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = null;

        await adapter.NotifySiteAddedAsync(
            app,
            "ors",
            "001",
            null,
            true,
            TestContext.Current.CancellationToken
        );

        handler.CapturedRequest.Should().BeNull();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_OrsSite_PostsToSiteAddedUrlWithNullSiteNumber()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        await adapter.NotifySiteAddedAsync(
            app,
            "ors",
            "001",
            null,
            true,
            TestContext.Current.CancellationToken
        );

        handler
            .CapturedRequest!.RequestUri!.ToString()
            .Should()
            .Be($"{TestUrl}/work-items/re-accreditation/{workItemId}/site-added");
        handler.CapturedRequest!.Method.Should().Be(HttpMethod.Post);

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;
        root.GetProperty("siteType").GetString().Should().Be("ors");
        root.GetProperty("orsId").GetString().Should().Be("001");
        root.GetProperty("siteNumber").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("isNewSite").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_InterimSite_PostsSiteNumber()
    {
        var workItemId = Guid.NewGuid();
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = workItemId;

        await adapter.NotifySiteAddedAsync(
            app,
            "interim",
            "001",
            "SN-0002",
            true,
            TestContext.Current.CancellationToken
        );

        var doc = JsonDocument.Parse(handler.CapturedRequestBody!);
        var root = doc.RootElement;
        root.GetProperty("siteType").GetString().Should().Be("interim");
        root.GetProperty("siteNumber").GetString().Should().Be("SN-0002");
    }

    // Covers the `userId ?? OrganisationId` fallback and BuildRequest's
    // `!string.IsNullOrEmpty(userName)` false branch for this method's own request-building code.
    [Fact]
    public async Task NotifySiteAddedAsync_NoSubmittedBy_UsesOrganisationIdAsUserId()
    {
        var (adapter, handler) = CreateAdapter();
        var app = CreateTestApplication();
        app.SubmittedBy = null;
        app.CaseManagementWorkItemId = Guid.NewGuid();

        await adapter.NotifySiteAddedAsync(
            app,
            "ors",
            "001",
            null,
            true,
            TestContext.Current.CancellationToken
        );

        var request = handler.CapturedRequest!;
        request.Headers.GetValues("x-cdp-user-id").Should().ContainSingle("12345");
        request.Headers.Contains("x-cdp-user-name").Should().BeFalse();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_EmptyUrl_DoesNotThrow()
    {
        var (adapter, _) = CreateAdapter(url: "");
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var act = async () => await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_ManagementBeUnreachable_DoesNotThrow()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory
            .CreateClient("DefaultClient")
            .Returns(new HttpClient(new ThrowingHttpMessageHandler()));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var act = async () => await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_NonSuccessResponse_DoesNotThrow()
    {
        var config = Options.Create(new CaseWorkingApiConfig { Url = TestUrl });
        var handler = new CapturingHttpMessageHandler(
            HttpStatusCode.InternalServerError,
            new { title = "Error" }
        );
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("DefaultClient").Returns(new HttpClient(handler));
        var adapter = new HttpCaseWorkingApiAdapter(
            httpClientFactory,
            config,
            EnabledNullLogger<HttpCaseWorkingApiAdapter>.Instance
        );
        var app = CreateTestApplication();
        app.CaseManagementWorkItemId = Guid.NewGuid();

        var act = async () => await adapter.NotifySiteAddedAsync(app, "ors", "001", null, true);

        await act.Should().NotThrowAsync();
    }

    #region Test infrastructure

    internal class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly object _body;

        public HttpRequestMessage? CapturedRequest { get; private set; }
        public string? CapturedRequestBody { get; private set; }

        public CapturingHttpMessageHandler(HttpStatusCode status, object body)
        {
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            CapturedRequest = request;
            if (request.Content != null)
                CapturedRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status) { Content = JsonContent.Create(_body) };
        }
    }

    internal class RawBodyHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RawBodyHttpMessageHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(
                new HttpResponseMessage(_status) { Content = new StringContent(_body) }
            );
        }
    }

    internal class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            throw new HttpRequestException("Simulated network failure");
        }
    }

    // Simulates HttpClient.Timeout elapsing: real HttpClient reports this as a
    // TaskCanceledException whose inner exception is a TimeoutException, distinct in shape from
    // a plain caller-driven OperationCanceledException.
    internal class TimeoutHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.",
                new TimeoutException()
            );
        }
    }

    #endregion

    #region RA-316 — charge amount / payment reference wire contract

    // ManagementBe displays whatever integer we send on the duly-making page and does NOT
    // recompute it, so these tests pin the exact names, JSON types and units it agreed to
    // consume: `chargeAmountPence` (integer, minor units) and `paymentReference` (string), both
    // at the TOP LEVEL of `payload`. A rename or a unit slip here shows the regulator a charge
    // that is silently 100x wrong, which no other test in this file would catch.

    private static AccreditationApplicationModel ApplicationWithCharge(
        PlannedTonnageBand? band,
        int selectedSites = 0
    )
    {
        var application = CreateTestApplication();
        application.Prns.PlannedTonnageBand = band;
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = Enumerable
                .Range(1, selectedSites)
                .Select(i => new OverseasSiteModel { SiteId = i, SiteName = $"Site {i}" })
                .ToList(),
        };
        return application;
    }

    [Fact]
    public async Task SubmitApplicationAsync_SendsChargeAmountAsIntegerPenceAtPayloadTopLevel()
    {
        // £3,276 tonnage + £328 x 2 sites = £3,932 -> 393200 pence.
        var payload = await CapturedSubmitPayload(
            ApplicationWithCharge(PlannedTonnageBand.UpTo10000, selectedSites: 2)
        );

        var charge = payload.GetProperty("chargeAmountPence");
        charge.ValueKind.Should().Be(JsonValueKind.Number, "the contract is a JSON number");
        charge.GetInt32().Should().Be(393_200);

        // Pin the unit explicitly: pounds would serialise as 3932 and look superficially sane.
        charge.GetInt32().Should().NotBe(3_932);
    }

    [Theory]
    [InlineData(PlannedTonnageBand.UpTo500, 54_600)]
    [InlineData(PlannedTonnageBand.UpTo5000, 218_400)]
    [InlineData(PlannedTonnageBand.UpTo10000, 327_600)]
    [InlineData(PlannedTonnageBand.Over10000, 396_500)]
    public async Task SubmitApplicationAsync_EveryTonnageBand_ReachesTheWireInPence(
        PlannedTonnageBand band,
        int expectedPence
    )
    {
        var payload = await CapturedSubmitPayload(ApplicationWithCharge(band));

        payload.GetProperty("chargeAmountPence").GetInt32().Should().Be(expectedPence);
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoTonnageBand_OmitsChargeRatherThanFailingTheSubmission()
    {
        // The chosen missing-band behaviour: omit the field, never throw. The submission itself
        // must still succeed — a display-only field must not be able to block accreditation.
        var (adapter, handler) = CreateAdapter();
        var result = await adapter.SubmitApplicationAsync(
            ApplicationWithCharge(null, 2),
            TestContext.Current.CancellationToken
        );

        result.ApplicationReference.Should().Be(TestApplicationReference);

        var payload = JsonDocument
            .Parse(handler.CapturedRequestBody!)
            .RootElement.GetProperty("payload");
        payload
            .TryGetProperty("chargeAmountPence", out _)
            .Should()
            .BeFalse("a null charge is dropped by WhenWritingNull, not sent as null or zero");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoPaymentReferenceSuppliedByOperator_OmitsIt()
    {
        // RA-503: PaymentReference is captured from SubmitRequest (the operator's real,
        // frontend-computed bank reference) - a caller that predates this sends none, and it
        // must be ABSENT rather than an empty string or a stand-in such as ApplicationReference
        // or registrationReference.
        var application = ApplicationWithCharge(PlannedTonnageBand.UpTo500);
        application.PaymentReference = null;

        var payload = await CapturedSubmitPayload(application);

        payload.TryGetProperty("paymentReference", out _).Should().BeFalse();
        payload.GetProperty("registrationNumber").GetString().Should().Be("EPR-100023");
    }

    // RA-503: PaymentReference is the operator's real, nation-specific bank reference
    // (buildPaymentReference in epr-register-enrol-frontend) - it must be sent as-is, never
    // substituted with the backend-generated ApplicationReference (which is a different value
    // entirely, and null at initial-submit time regardless).
    [Fact]
    public async Task SubmitApplicationAsync_WithPaymentReference_SendsItAsPaymentReferenceString()
    {
        var application = ApplicationWithCharge(PlannedTonnageBand.UpTo500);
        application.PaymentReference = "PR/PK/REP/500500";
        application.ApplicationReference = null;

        var payload = await CapturedSubmitPayload(application);

        var reference = payload.GetProperty("paymentReference");
        reference.ValueKind.Should().Be(JsonValueKind.String);
        reference.GetString().Should().Be("PR/PK/REP/500500");
    }

    [Fact]
    public async Task ResumeFromQueryAsync_SendsRecomputedChargeAlongsideSectionsNotInsideThem()
    {
        // Answering a query can change the band or the site count, and duly making happens after
        // the query is answered — so the charge must be recomputed here, not frozen at submit.
        var application = ApplicationWithCharge(PlannedTonnageBand.Over10000, selectedSites: 1);
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.ApplicationReference = TestApplicationReference;
        // RA-503: PaymentReference (the operator's real bank reference, persisted since the
        // original Submit) must be resent as-is, never the backend-generated ApplicationReference.
        application.PaymentReference = "PR/PK/REP/500500";

        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            application,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["prn-tonnage"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var root = JsonDocument.Parse(handler.CapturedRequestBody!).RootElement;

        // £3,965 + £328 = £4,293 -> 429300 pence.
        root.GetProperty("chargeAmountPence").GetInt32().Should().Be(429_300);
        root.GetProperty("paymentReference").GetString().Should().Be("PR/PK/REP/500500");

        // Siblings of sections, never entries within it: the sections dictionary is keyed by
        // section name and its projections must stay identical to BuildPayload's (RA-292 AC04).
        var sections = root.GetProperty("sections");
        sections
            .TryGetProperty("Prns", out _)
            .Should()
            .BeTrue("otherwise the absence assertions below would pass vacuously");
        sections.TryGetProperty("chargeAmountPence", out _).Should().BeFalse();
        sections.TryGetProperty("paymentReference", out _).Should().BeFalse();
    }

    // RA-503 PR review (masante): PaymentReference is a brand-new field with no backfill for an
    // application submitted before this deploy - such an application has ApplicationReference
    // (populated since its original submission response) but no PaymentReference at all. Without
    // this fallback, a resume-from-query round trip on that application would regress from
    // sending its ApplicationReference (the pre-RA-503 behaviour) to sending nothing.
    [Fact]
    public async Task ResumeFromQueryAsync_NullPaymentReference_FallsBackToApplicationReference()
    {
        var application = ApplicationWithCharge(PlannedTonnageBand.UpTo500);
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.ApplicationReference = TestApplicationReference;
        application.PaymentReference = null;

        var (adapter, handler) = CreateAdapter();
        await adapter.ResumeFromQueryAsync(
            application,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["prn-tonnage"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        var root = JsonDocument.Parse(handler.CapturedRequestBody!).RootElement;
        root.GetProperty("paymentReference").GetString().Should().Be(TestApplicationReference);
    }

    [Fact]
    public async Task ResumeFromQueryAsync_NoTonnageBand_OmitsChargeAndStillSucceeds()
    {
        var application = ApplicationWithCharge(null, selectedSites: 3);
        application.CaseManagementWorkItemId = Guid.NewGuid();
        application.ApplicationReference = null;

        var (adapter, handler) = CreateAdapter();
        var result = await adapter.ResumeFromQueryAsync(
            application,
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Manager",
            },
            ["prn-tonnage"],
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.IsSuccess.Should().BeTrue();

        var root = JsonDocument.Parse(handler.CapturedRequestBody!).RootElement;
        root.TryGetProperty("chargeAmountPence", out _).Should().BeFalse();
        root.TryGetProperty("paymentReference", out _).Should().BeFalse();
        root.GetProperty("sections").TryGetProperty("Prns", out _).Should().BeTrue();
    }

    [Fact]
    public async Task SubmitApplicationAsync_DeselectedSitesDoNotAddToTheChargeOnTheWire()
    {
        var application = ApplicationWithCharge(PlannedTonnageBand.UpTo5000, selectedSites: 3);
        application.OverseasSites!.Sites[1].Selected = false;

        var payload = await CapturedSubmitPayload(application);

        // £2,184 + £328 x 2 still-selected = £2,840 -> 284000 pence.
        payload.GetProperty("chargeAmountPence").GetInt32().Should().Be(284_000);
    }

    [Fact]
    public async Task SubmitApplicationAsync_ChargeFieldsDoNotDisturbTheHmacSignature()
    {
        // The v3 canonical payload covers clientId/userId/userName/timestamp/nonce only — the
        // body is NOT signed — so adding body fields cannot invalidate the signature. Pinned
        // because the opposite assumption would make every payload change a breaking auth change.
        const string secret = "test-secret-key";
        var (adapter, handler) = CreateAdapter(sharedSecret: secret);
        await adapter.SubmitApplicationAsync(
            ApplicationWithCharge(PlannedTonnageBand.Over10000, selectedSites: 4),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var request = handler.CapturedRequest!;
        var expected = HttpCaseWorkingApiAdapter.ComputeSignature(
            secret,
            TestClientId,
            "jane@example.com",
            "Jane Smith",
            request.Headers.GetValues("x-cdp-auth-timestamp").Single(),
            request.Headers.GetValues("x-cdp-auth-nonce").Single()
        );

        request.Headers.GetValues("x-cdp-auth-signature").Single().Should().Be(expected);

        JsonDocument
            .Parse(handler.CapturedRequestBody!)
            .RootElement.GetProperty("payload")
            .GetProperty("chargeAmountPence")
            .GetInt32()
            .Should()
            .Be(527_700);
    }

    #endregion
}
