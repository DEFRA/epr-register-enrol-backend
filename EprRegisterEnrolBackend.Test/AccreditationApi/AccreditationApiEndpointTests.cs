using System.Net;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

// Drives the accreditation API the way a service client does: through the real host and auth
// pipeline (outside Development, where the other schemes' bypasses would otherwise hide a
// missing gate), with signed requests, against a real ephemeral Mongo.
public class AccreditationApiEndpointTests : IDisposable
{
    private const string ReExClientId = "epr-backend";
    private const string ReExSecret = "reex-integration-secret";
    private const string OtherClientId = "another-consumer";
    private const string OtherSecret = "another-integration-secret";
    private const string ManagementBeClientId = "epr-register-enrol-management-be";
    private const string ManagementBeSecret = "integration-management-be-secret";

    private const int Org = 500027;
    private const string Registration = "R27EX5000270012AL";
    private const string AccreditationPath =
        "/v1/organisations/500027/registrations/R27EX5000270012AL/accreditations/2027";

    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApiEndpointTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "accreditation_api",
            Environments.Production,
            new Dictionary<string, string?>
            {
                ["ServiceClients:Clients:0:Name"] = "reex",
                ["ServiceClients:Clients:0:ClientId"] = ReExClientId,
                ["ServiceClients:Clients:1:Name"] = "other",
                ["ServiceClients:Clients:1:ClientId"] = OtherClientId,
                ["AUTH_SHARED_SECRET:REEX"] = ReExSecret,
                ["AUTH_SHARED_SECRET:OTHER"] = OtherSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = ManagementBeSecret,
                ["AUTH_SHARED_SECRET:FRONTEND"] = "integration-frontend-secret",
                ["CaseWorking:UseStub"] = "true",
            }
        );
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task Seed(
        int year = 2027,
        ApplicationStatus status = ApplicationStatus.Approved,
        string? accreditation = "A27EX5000270012AL",
        string registration = Registration,
        int orgId = Org,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var application = new AccreditationApplicationModel
        {
            OrganisationId = "reex-object-id",
            OrgId = orgId,
            OrganisationName = "Egger (UK) Limited",
            Year = year,
            MaterialType = MaterialType.Aluminium,
            ApplicationStatus = status,
            RegistrationReference = registration,
            AccreditationReference = accreditation,
            IsExporter = true,
            WasteProcessingType = "exporter",
            Nation = Nation.England,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    Site(1, "001", selected: true, validFrom: "2026-03-14T00:00:00.000Z"),
                    Site(2, "002", selected: true, validFrom: null),
                    Site(3, "003", selected: false, validFrom: null),
                ],
            },
        };
        configure?.Invoke(application);
        var created = await _factory
            .Services.GetRequiredService<IAccreditationApplicationPersistence>()
            .CreateAsync(application);
        created.Should().NotBeNull();
    }

    private static OverseasSiteModel Site(int id, string orsId, bool selected, string? validFrom) =>
        new()
        {
            SiteId = id,
            OrsId = orsId,
            SiteName = $"Site {orsId}",
            AddressLine1 = $"{id} Hafenstrasse",
            TownOrCity = "Hamburg",
            Country = "Germany",
            Selected = selected,
            ValidFrom = validFrom,
        };

    private Task<HttpResponseMessage> Get(
        string path,
        string clientId = ReExClientId,
        string secret = ReExSecret
    ) => _client.SendAsync(ServiceClientSigner.Request(HttpMethod.Get, path, clientId, secret), Ct);

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    private static async Task AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code
    )
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await Json(response);
        body.GetProperty("status").GetInt32().Should().Be((int)status);
        body.GetProperty("code").GetString().Should().Be(code);
        body.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ---- GET accreditation by year ----

    [Fact]
    public async Task GrantedAccreditation_IsServed()
    {
        await Seed();

        var response = await Get(AccreditationPath);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = await Json(response);
        body.GetProperty("accreditationReference").GetString().Should().Be("A27EX5000270012AL");
        body.GetProperty("registrationNumber").GetString().Should().Be(Registration);
        body.GetProperty("year").GetInt32().Should().Be(2027);
        body.GetProperty("status").GetString().Should().Be("approved");
        body.GetProperty("material").GetString().Should().Be("Aluminium");
        body.GetProperty("wasteProcessingType").GetString().Should().Be("exporter");
        body.GetProperty("overseasSites").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("001", "002", "003");
    }

    [Fact]
    public async Task OtherClients_ReadTheSameData()
    {
        await Seed();

        var response = await Get(AccreditationPath, OtherClientId, OtherSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OnlyTheRequestedYear_IsServed()
    {
        await Seed(year: 2027, accreditation: "A27");
        await Seed(year: 2028, accreditation: "A28");

        var body = await Json(
            await Get($"/v1/organisations/{Org}/registrations/{Registration}/accreditations/2028")
        );

        body.GetProperty("accreditationReference").GetString().Should().Be("A28");
        body.GetProperty("year").GetInt32().Should().Be(2028);
    }

    [Theory]
    [InlineData(500099, Registration, 2027)] // another organisation
    [InlineData(Org, "R27OTHER", 2027)] // another registration
    [InlineData(Org, Registration, 2028)] // another year
    [InlineData(Org, Registration, 2026)] // before the first served year
    public async Task NoGrantedAccreditation_Is404(int org, string registration, int year)
    {
        await Seed();

        var response = await Get(
            $"/v1/organisations/{org}/registrations/{registration}/accreditations/{year}"
        );

        await AssertProblem(response, HttpStatusCode.NotFound, "accreditation_not_found");
    }

    [Fact]
    public async Task OrganisationRegistrationAndYear_AreNamedInTheNotFoundDetail()
    {
        var response = await Get(AccreditationPath);

        var detail = (await Json(response)).GetProperty("detail").GetString();
        detail.Should().Contain($"{Org}").And.Contain(Registration).And.Contain("2027");
    }

    [Fact]
    public async Task Year_BeforeTheFirstServedYear_IsNotServed_EvenIfGranted()
    {
        await Seed(year: 2026, accreditation: "A26");

        var response = await Get(
            $"/v1/organisations/{Org}/registrations/{Registration}/accreditations/2026"
        );

        await AssertProblem(response, HttpStatusCode.NotFound, "accreditation_not_found");
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Started)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.DulyMade)]
    [InlineData(ApplicationStatus.Queried)]
    [InlineData(ApplicationStatus.Updated)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task ApplicationNotGranted_IsNotServed(ApplicationStatus status)
    {
        await Seed(status: status);

        var response = await Get(AccreditationPath);

        await AssertProblem(response, HttpStatusCode.NotFound, "accreditation_not_found");
    }

    [Fact]
    public async Task ApprovedApplication_WithNoAccreditationNumber_IsNotServed()
    {
        await Seed(accreditation: null);

        var response = await Get(AccreditationPath);

        await AssertProblem(response, HttpStatusCode.NotFound, "accreditation_not_found");
    }

    [Fact]
    public async Task TwoGrantedApplications_ServeTheMostRecentlyEdited()
    {
        await Seed(
            accreditation: "A27-OLDER",
            configure: a => a.DateLastEdited = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        await Seed(
            accreditation: "A27-NEWER",
            configure: a => a.DateLastEdited = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        );

        var body = await Json(await Get(AccreditationPath));

        body.GetProperty("accreditationReference").GetString().Should().Be("A27-NEWER");
    }

    [Theory]
    [InlineData("abc", "R1", "2027", "invalid_organisation_number")]
    [InlineData("-5", "R1", "2027", "invalid_organisation_number")]
    [InlineData("99999999999", "R1", "2027", "invalid_organisation_number")]
    [InlineData("500027", "bad%20number", "2027", "invalid_registration_number")]
    [InlineData("500027", "R1", "27", "invalid_year")]
    [InlineData("500027", "R1", "twenty", "invalid_year")]
    [InlineData("500027", "R1", "20277", "invalid_year")]
    public async Task InvalidPath_Is400(string org, string registration, string year, string code)
    {
        var response = await Get(
            $"/v1/organisations/{org}/registrations/{registration}/accreditations/{year}"
        );

        await AssertProblem(response, HttpStatusCode.BadRequest, code);
    }

    // ---- GET overseas sites ----

    [Fact]
    public async Task OverseasSites_AreListed_WithTheirApproval()
    {
        await Seed();

        var body = await Json(await Get($"{AccreditationPath}/overseas-sites"));

        body.GetProperty("pageNumber").GetInt32().Should().Be(1);
        body.GetProperty("limit").GetInt32().Should().Be(20);
        body.GetProperty("totalItems").GetInt32().Should().Be(3);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("orsId").GetString()).Should().Equal("001", "002", "003");
        items[0].GetProperty("validFrom").GetString().Should().Be("2026-03-14T00:00:00.000Z");
        items[0].GetProperty("selected").GetBoolean().Should().BeTrue();
        items[1].TryGetProperty("validFrom", out _).Should().BeFalse();
        items[2].GetProperty("selected").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("true", new[] { "001", "002" })]
    [InlineData("false", new[] { "003" })]
    public async Task OverseasSites_CanBeFilteredBySelected(string selected, string[] expected)
    {
        await Seed();

        var body = await Json(await Get($"{AccreditationPath}/overseas-sites?selected={selected}"));

        body.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("orsId").GetString())
            .Should().Equal(expected);
        body.GetProperty("totalItems").GetInt32().Should().Be(expected.Length);
    }

    [Fact]
    public async Task OverseasSites_ArePaged()
    {
        await Seed();

        var body = await Json(await Get($"{AccreditationPath}/overseas-sites?pageNumber=2&limit=2"));

        body.GetProperty("pageNumber").GetInt32().Should().Be(2);
        body.GetProperty("limit").GetInt32().Should().Be(2);
        body.GetProperty("totalItems").GetInt32().Should().Be(3);
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("orsId").GetString())
            .Should().Equal("003");
    }

    [Theory]
    [InlineData("pageNumber=0", "invalid_paging")]
    [InlineData("pageNumber=x", "invalid_paging")]
    [InlineData("limit=0", "invalid_paging")]
    [InlineData("limit=101", "invalid_paging")]
    [InlineData("selected=maybe", "invalid_selected")]
    public async Task OverseasSites_InvalidQuery_Is400(string query, string code)
    {
        await Seed();

        var response = await Get($"{AccreditationPath}/overseas-sites?{query}");

        await AssertProblem(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task OverseasSites_OfAnUnknownAccreditation_Is404()
    {
        var response = await Get($"{AccreditationPath}/overseas-sites");

        await AssertProblem(response, HttpStatusCode.NotFound, "accreditation_not_found");
    }

    [Fact]
    public async Task OverseasSite_IsServedByItsOrsId()
    {
        await Seed();

        var response = await Get($"{AccreditationPath}/overseas-sites/002");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("orsId").GetString().Should().Be("002");
        body.GetProperty("siteName").GetString().Should().Be("Site 002");
    }

    [Fact]
    public async Task OverseasSite_NotOnTheAccreditation_Is404()
    {
        await Seed();

        var response = await Get($"{AccreditationPath}/overseas-sites/999");

        await AssertProblem(response, HttpStatusCode.NotFound, "overseas_site_not_found");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("abcd")]
    [InlineData("0001")]
    public async Task OverseasSite_WithABadOrsId_Is400(string orsId)
    {
        await Seed();

        var response = await Get($"{AccreditationPath}/overseas-sites/{orsId}");

        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_ors_id");
    }

    // ---- authentication ----

    public static TheoryData<string> Routes() =>
        new()
        {
            AccreditationPath,
            $"{AccreditationPath}/overseas-sites",
            $"{AccreditationPath}/overseas-sites/001",
        };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task NoCredentials_Is401_OnEveryRoute(string path)
    {
        var response = await _client.GetAsync(path, Ct);

        await AssertProblem(response, HttpStatusCode.Unauthorized, "missing_header");
        response.Headers.WwwAuthenticate.ToString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task WrongSecret_Is401()
    {
        await Seed();

        var response = await Get(AccreditationPath, secret: "not-the-secret");

        await AssertProblem(response, HttpStatusCode.Unauthorized, "invalid_signature");
    }

    [Fact]
    public async Task TamperedSignature_Is401()
    {
        var request = ServiceClientSigner.Request(
            HttpMethod.Get,
            AccreditationPath,
            ReExClientId,
            ReExSecret,
            signatureOverride: "dGFtcGVyZWQ="
        );

        await AssertProblem(
            await _client.SendAsync(request, Ct),
            HttpStatusCode.Unauthorized,
            "invalid_signature"
        );
    }

    [Theory]
    [InlineData(-10)]
    [InlineData(10)]
    public async Task ExpiredSignature_Is401(int minutesFromNow)
    {
        var request = ServiceClientSigner.Request(
            HttpMethod.Get,
            AccreditationPath,
            ReExClientId,
            ReExSecret,
            timestamp: ServiceClientSigner.Timestamp(DateTime.UtcNow.AddMinutes(minutesFromNow))
        );

        await AssertProblem(
            await _client.SendAsync(request, Ct),
            HttpStatusCode.Unauthorized,
            "expired_timestamp"
        );
    }

    [Fact]
    public async Task ReusedNonce_Is401_OnTheSecondRequest()
    {
        await Seed();
        var timestamp = ServiceClientSigner.Timestamp();
        var nonce = ServiceClientSigner.NewNonce();

        HttpRequestMessage Signed() =>
            ServiceClientSigner.Request(
                HttpMethod.Get,
                AccreditationPath,
                ReExClientId,
                ReExSecret,
                timestamp,
                nonce
            );

        var first = await _client.SendAsync(Signed(), Ct);
        var second = await _client.SendAsync(Signed(), Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertProblem(second, HttpStatusCode.Unauthorized, "reused_nonce");
    }

    [Fact]
    public async Task UnknownClient_Is401()
    {
        var response = await Get(AccreditationPath, "nobody", "whatever");

        await AssertProblem(response, HttpStatusCode.Unauthorized, "unknown_client");
    }

    [Fact]
    public async Task OneClientsCredentials_CannotPassAsAnotherClient()
    {
        await Seed();

        var asReEx = await Get(AccreditationPath, ReExClientId, OtherSecret);
        var asOther = await Get(AccreditationPath, OtherClientId, ReExSecret);

        await AssertProblem(asReEx, HttpStatusCode.Unauthorized, "invalid_signature");
        await AssertProblem(asOther, HttpStatusCode.Unauthorized, "invalid_signature");
    }

    [Fact]
    public async Task ManagementBeCredentials_AreNotAcceptedOnTheseRoutes()
    {
        await Seed();

        var response = await Get(AccreditationPath, ManagementBeClientId, ManagementBeSecret);

        await AssertProblem(response, HttpStatusCode.Unauthorized, "unknown_client");
    }

    [Fact]
    public async Task FrontendBearerToken_IsNotAcceptedOnTheseRoutes()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AccreditationPath);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            "integration-frontend-secret"
        );

        await AssertProblem(
            await _client.SendAsync(request, Ct),
            HttpStatusCode.Unauthorized,
            "missing_header"
        );
    }

    // ---- read only ----

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task WritingToTheRoutes_IsNotAllowed(string method)
    {
        await Seed();

        foreach (var path in new[] { AccreditationPath, $"{AccreditationPath}/overseas-sites", $"{AccreditationPath}/overseas-sites/001" })
        {
            var response = await _client.SendAsync(
                ServiceClientSigner.Request(new HttpMethod(method), path, ReExClientId, ReExSecret),
                Ct
            );

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, path);
        }
    }

    [Theory]
    [InlineData("POST", "/api/v1/accreditation-applications/case-management/5e33055f-c610-4cbf-ae5a-7a7843fd08b9/status")]
    [InlineData("POST", "/api/v1/accreditation-applications/case-management/5e33055f-c610-4cbf-ae5a-7a7843fd08b9/query")]
    [InlineData("PATCH", "/api/v1/accreditation-applications/org-1/app-1/prns")]
    [InlineData("POST", "/api/v1/accreditation-applications/org-1/app-1/submit")]
    public async Task ServiceClientCredentials_DoNotOpenAnyRouteThatRecordsChanges(
        string method,
        string path
    )
    {
        var response = await _client.SendAsync(
            ServiceClientSigner.Request(new HttpMethod(method), path, ReExClientId, ReExSecret),
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- traceability ----

    [Fact]
    public async Task EveryCall_IsLoggedWithTheClient_AndNeverTheSecret()
    {
        await Seed();
        var logs = new CapturingLoggerFactory();
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerFactory>(logs))
        );
        using var client = factory.CreateClient();
        var request = ServiceClientSigner.Request(
            HttpMethod.Get,
            AccreditationPath,
            ReExClientId,
            ReExSecret
        );
        var signature = request.Headers.GetValues("x-cdp-auth-signature").Single();
        var nonce = request.Headers.GetValues("x-cdp-auth-nonce").Single();

        var response = await client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var access = logs.Entries.Where(e => e.Category.Contains("ServiceClientAccess")).ToList();
        access.Should().ContainSingle();
        access[0].Message.Should().Contain($"clientId={ReExClientId}");
        access[0].Message.Should().Contain("clientName=reex");
        access[0].Message.Should().Contain("status=200");
        access[0].Message.Should().Contain($"organisationNumber={Org}");
        access[0].Message.Should().Contain($"registrationNumber={Registration}");
        access[0].Message.Should().Contain("year=2027");

        var everything = string.Join('\n', logs.Entries.Select(e => e.Message));
        everything.Should().NotContain(ReExSecret);
        everything.Should().NotContain(signature);
        everything.Should().NotContain(nonce);
    }
}
