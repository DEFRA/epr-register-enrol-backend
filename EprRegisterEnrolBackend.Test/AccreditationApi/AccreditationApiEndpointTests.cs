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
        body.GetProperty("organisationNumber").GetInt32().Should().Be(Org);
        body.GetProperty("accreditationNumber").GetString().Should().Be("A27EX5000270012AL");
        body.GetProperty("registrationNumber").GetString().Should().Be(Registration);
        body.GetProperty("accreditationYear").GetInt32().Should().Be(2027);
        body.GetProperty("status").GetString().Should().Be("approved");
        body.GetProperty("material").GetString().Should().Be("Aluminium");
        body.GetProperty("wasteProcessingType").GetString().Should().Be("exporter");
        body.GetProperty("signatories").GetArrayLength().Should().Be(0);
        var history = body.GetProperty("statusHistory").EnumerateArray().Single();
        history.GetProperty("status").GetString().Should().Be("approved");
        history.GetProperty("effectiveDate").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}$");
        history.GetProperty("recordedAt").GetDateTime().Should().BeAfter(DateTime.MinValue);
        body.GetProperty("tonnageBandHistory").GetArrayLength().Should().Be(0);
        body.TryGetProperty("overseasSites", out _).Should().BeFalse();
        body.TryGetProperty("accreditationReference", out _).Should().BeFalse();
        body.TryGetProperty("businessPlan", out _).Should().BeFalse();
        body.TryGetProperty("prnIssuance", out _).Should().BeFalse();
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

        body.GetProperty("accreditationNumber").GetString().Should().Be("A28");
        body.GetProperty("accreditationYear").GetInt32().Should().Be(2028);
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

        body.GetProperty("accreditationNumber").GetString().Should().Be("A27-NEWER");
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
    public async Task OverseasSites_AreListed_OnlyThoseIncludedInTheAccreditation()
    {
        await Seed();

        var body = await Json(await Get($"{AccreditationPath}/overseas-sites"));

        body.GetProperty("pageNumber").GetInt32().Should().Be(1);
        body.GetProperty("limit").GetInt32().Should().Be(20);
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("orsId").GetString()).Should().Equal("001", "002");
        items[0].GetProperty("validFrom").GetDateTime().Should().Be(
            new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc)
        );
        items[0].GetProperty("status").GetString().Should().Be("approved");
        items[0].GetProperty("statusHistory").EnumerateArray().Single()
            .GetProperty("effectiveDate").GetString().Should().Be("2026-03-14");
        items[0].GetProperty("interimSites").GetArrayLength().Should().Be(0);
        items[1].TryGetProperty("validFrom", out _).Should().BeFalse();
        foreach (var item in items)
        {
            item.TryGetProperty("selected", out _).Should().BeFalse();
            item.TryGetProperty("interimSiteIds", out _).Should().BeFalse();
            item.TryGetProperty("isNewSite", out _).Should().BeFalse();
            item.TryGetProperty("code1", out _).Should().BeFalse();
        }
    }

    [Fact]
    public async Task OverseasSites_ServeInterimSitesInline_WithdrawnOnlyWhenAskedFor()
    {
        await Seed(
            configure: a =>
                a.OverseasSites!.Sites[0].InterimSites =
                [
                    Interim(11, removed: false),
                    Interim(12, removed: true),
                ]
        );

        var defaults = await Json(await Get($"{AccreditationPath}/overseas-sites/001"));
        var all = await Json(
            await Get($"{AccreditationPath}/overseas-sites/001?includeWithdrawn=true")
        );

        defaults.GetProperty("interimSites").EnumerateArray()
            .Select(i => i.GetProperty("siteId").GetInt32()).Should().Equal(11);
        var interims = all.GetProperty("interimSites").EnumerateArray().ToList();
        interims.Select(i => i.GetProperty("siteId").GetInt32()).Should().Equal(11, 12);
        interims[0].GetProperty("siteNumber").GetString().Should().Be("011");
        interims[0].GetProperty("address").GetProperty("line1").GetString().Should().Be("Line 1");
        interims[0].GetProperty("contact").GetProperty("email").GetString().Should().Be("e@example.com");
        interims[0].TryGetProperty("removedAt", out _).Should().BeFalse();
        interims[1].GetProperty("removedAt").GetDateTime().Should().BeAfter(DateTime.MinValue);
    }

    [Fact]
    public async Task OverseasSites_ListHonoursIncludeWithdrawn()
    {
        await Seed(configure: a => a.OverseasSites!.Sites[1].InterimSites = [Interim(21, removed: true)]);

        var hidden = await Json(await Get($"{AccreditationPath}/overseas-sites"));
        var shown = await Json(await Get($"{AccreditationPath}/overseas-sites?includeWithdrawn=true"));

        hidden.GetProperty("items")[1].GetProperty("interimSites").GetArrayLength().Should().Be(0);
        shown.GetProperty("items")[1].GetProperty("interimSites").GetArrayLength().Should().Be(1);
    }

    private static InterimSiteModel Interim(int id, bool removed) =>
        new()
        {
            SiteId = id,
            SiteNumber = id.ToString("000"),
            Country = "Germany",
            SiteName = $"Interim {id}",
            AddressLine1 = "Line 1",
            TownOrCity = "Town",
            ContactName = "n",
            ContactEmail = "e@example.com",
            ContactPhone = "1",
            RemovedAt = removed ? DateTime.UtcNow : null,
        };

    [Fact]
    public async Task OverseasSites_ArePaged()
    {
        await Seed();

        var body = await Json(
            await Get($"{AccreditationPath}/overseas-sites?pageNumber=2&limit=1")
        );

        body.GetProperty("pageNumber").GetInt32().Should().Be(2);
        body.GetProperty("limit").GetInt32().Should().Be(1);
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        body.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("orsId").GetString())
            .Should()
            .Equal("002");
    }

    [Theory]
    [InlineData("pageNumber=0", "invalid_paging")]
    [InlineData("pageNumber=x", "invalid_paging")]
    [InlineData("limit=0", "invalid_paging")]
    [InlineData("limit=101", "invalid_paging")]
    [InlineData("includeWithdrawn=maybe", "invalid_include_withdrawn")]
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

    [Theory]
    [InlineData("999")] // unknown
    [InlineData("003")] // registered, but not included in the accreditation
    public async Task OverseasSite_NotOnTheAccreditation_Is404(string orsId)
    {
        await Seed();

        var response = await Get($"{AccreditationPath}/overseas-sites/{orsId}");

        await AssertProblem(response, HttpStatusCode.NotFound, "overseas_site_not_found");
    }

    [Fact]
    public async Task OverseasSite_WithABadIncludeWithdrawn_Is400()
    {
        await Seed();

        var response = await Get($"{AccreditationPath}/overseas-sites/001?includeWithdrawn=maybe");

        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_include_withdrawn");
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

    // ---- search ----

    [Fact]
    public async Task Search_ReturnsSummaries_AcrossOrganisations()
    {
        await Seed(
            accreditation: "A27EX5000270012AL",
            configure: a =>
            {
                a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo5000;
            }
        );
        await Seed(
            accreditation: "A27RE5000280012GL",
            registration: "R27RE5000280012GL",
            orgId: 500028,
            configure: a =>
            {
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
                a.IsExporter = false;
                a.WasteProcessingType = "reprocessor";
            }
        );

        var response = await Get("/v1/accreditations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("pageNumber").GetInt32().Should().Be(1);
        body.GetProperty("limit").GetInt32().Should().Be(20);
        body.GetProperty("totalItems").GetInt32().Should().Be(2);
        var items = body.GetProperty("items").EnumerateArray().ToList();
        items
            .Select(i => i.GetProperty("accreditationNumber").GetString())
            .Should()
            .Equal("A27EX5000270012AL", "A27RE5000280012GL");

        var exporter = items[0];
        exporter.GetProperty("organisationNumber").GetInt32().Should().Be(Org);
        exporter.GetProperty("registrationNumber").GetString().Should().Be(Registration);
        exporter.GetProperty("accreditationYear").GetInt32().Should().Be(2027);
        exporter.GetProperty("material").GetString().Should().Be("Aluminium");
        exporter.GetProperty("wasteProcessingType").GetString().Should().Be("exporter");
        exporter.GetProperty("status").GetString().Should().Be("approved");
        exporter.GetProperty("tonnageBand").GetString().Should().Be("up_to_5000");
        exporter.TryGetProperty("glassRecyclingProcess", out _).Should().BeFalse();

        var glass = items[1];
        glass.GetProperty("organisationNumber").GetInt32().Should().Be(500028);
        glass.GetProperty("glassRecyclingProcess").GetString().Should().Be("glass_re_melt");
        glass.GetProperty("wasteProcessingType").GetString().Should().Be("reprocessor");
        glass.TryGetProperty("tonnageBand", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Search_DoesNotExposeHistoryOrPersonalData()
    {
        await Seed();

        var item = (await Json(await Get("/v1/accreditations")))
            .GetProperty("items")
            .EnumerateArray()
            .Single();

        item.TryGetProperty("signatories", out _).Should().BeFalse();
        item.TryGetProperty("statusHistory", out _).Should().BeFalse();
        item.TryGetProperty("tonnageBandHistory", out _).Should().BeFalse();
        item.TryGetProperty("businessPlan", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Search_FiltersByYear()
    {
        await Seed(year: 2027, accreditation: "A27EX5000270012AL");
        await Seed(year: 2028, accreditation: "A28EX5000270012AL");

        var body = await Json(await Get("/v1/accreditations?year=2028"));

        body.GetProperty("totalItems").GetInt32().Should().Be(1);
        body.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("accreditationNumber").GetString())
            .Should()
            .Equal("A28EX5000270012AL");
    }

    [Fact]
    public async Task Search_OnlyServesGrantedAccreditations()
    {
        await Seed(accreditation: "A27EX5000270012AL");
        await Seed(status: ApplicationStatus.Submitted, accreditation: "A27EX5000270099AL");
        await Seed(accreditation: null, registration: "R27EX5000270013AL");
        await Seed(year: 2026, accreditation: "A26EX5000270012AL");
        await Seed(accreditation: "A27EX5000290012AL", orgId: 0, configure: a => a.OrgId = null);

        var body = await Json(await Get("/v1/accreditations"));

        body.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("accreditationNumber").GetString())
            .Should()
            .Equal("A27EX5000270012AL");
        body.GetProperty("totalItems").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Search_IsPaged()
    {
        await Seed(accreditation: "A27EX5000270011AL");
        await Seed(accreditation: "A27EX5000270012AL");
        await Seed(accreditation: "A27EX5000270013AL");

        var body = await Json(await Get("/v1/accreditations?pageNumber=2&limit=2"));

        body.GetProperty("pageNumber").GetInt32().Should().Be(2);
        body.GetProperty("limit").GetInt32().Should().Be(2);
        body.GetProperty("totalItems").GetInt32().Should().Be(3);
        body.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("accreditationNumber").GetString())
            .Should()
            .Equal("A27EX5000270013AL");
    }

    [Fact]
    public async Task Search_WithNothingGranted_IsAnEmptyPage()
    {
        var response = await Get("/v1/accreditations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("totalItems").GetInt32().Should().Be(0);
        body.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("year=abc", "invalid_year")]
    [InlineData("year=1999", "invalid_year")]
    [InlineData("year=-2027", "invalid_year")]
    [InlineData("pageNumber=0", "invalid_paging")]
    [InlineData("limit=101", "invalid_paging")]
    public async Task Search_InvalidQuery_Is400(string query, string code)
    {
        var response = await Get($"/v1/accreditations?{query}");

        await AssertProblem(response, HttpStatusCode.BadRequest, code);
    }

    // ---- authentication ----

    public static TheoryData<string> Routes() =>
        new()
        {
            "/v1/accreditations",
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

        foreach (
            var path in new[]
            {
                AccreditationPath,
                $"{AccreditationPath}/overseas-sites",
                $"{AccreditationPath}/overseas-sites/001",
            }
        )
        {
            var response = await _client.SendAsync(
                ServiceClientSigner.Request(new HttpMethod(method), path, ReExClientId, ReExSecret),
                Ct
            );

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, path);
        }
    }

    [Theory]
    [InlineData(
        "POST",
        "/api/v1/accreditation-applications/case-management/5e33055f-c610-4cbf-ae5a-7a7843fd08b9/status"
    )]
    [InlineData(
        "POST",
        "/api/v1/accreditation-applications/case-management/5e33055f-c610-4cbf-ae5a-7a7843fd08b9/query"
    )]
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
        // Logged once the response has completed, which can be after the client has it.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        List<CapturingLoggerFactory.Entry> access;
        while (
            (
                access = logs
                    .Entries.Where(e => e.Category.Contains("ServiceClientAccess"))
                    .ToList()
            ).Count == 0
            && DateTime.UtcNow < deadline
        )
        {
            await Task.Delay(25, Ct);
        }
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
