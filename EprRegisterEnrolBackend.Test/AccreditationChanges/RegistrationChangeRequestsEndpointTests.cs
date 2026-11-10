using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Organisation.Services;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Registrations;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-697: a registration-only operator's change requests, read by organisation, registration and material for the
// backend clock's year, with the registration's domain-model ORSs as its sites. Through the real host and auth
// pipeline against an ephemeral Mongo, with the domain model's Expand flag both off and on. The Expand-on runs are
// outside Development; the Expand-off runs use Development, because elsewhere the in-year changes flag needs
// DomainModel:DualWrite, which needs Expand.
// The stub ReEx registry's org 50003 (Delta Green) holds registration Reg50003.
public sealed class RegistrationChangeRequestsEndpointTests(MongoIntegrationFixture fixture)
{
    private const string FrontendSecret = "integration-frontend-secret";
    private const string Org = "50003";
    private const int Year = 2026;
    private static readonly string RegistrationId = FakeOrganisationPersistence.Reg50003.ToString();
    private static readonly string Path =
        $"/api/v1/accreditation-changes/{Org}/registrations/{RegistrationId}/plastic";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ChangeInfo Imported = new() { Reason = ChangeReason.ReExImport };

    private WebApplicationFactory<Program> Factory(
        bool expandEnabled = true,
        bool inYearChangesEnabled = true
    ) =>
        new EphemeralMongoTestFactory(
            fixture,
            "registration_change_requests",
            expandEnabled ? Environments.Production : Environments.Development,
            new Dictionary<string, string?>
            {
                ["AUTH_SHARED_SECRET:FRONTEND"] = FrontendSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-management-be-secret",
                ["CaseWorking:UseStub"] = "true",
                ["ReExApi:UseStub"] = "true",
                ["DomainModel:ExpandEnabled"] = expandEnabled ? "true" : "false",
                ["DomainModel:DualWrite"] = expandEnabled ? "true" : "false",
                [FeatureFlags.InYearChangesEnabledKey] = inYearChangesEnabled ? "true" : "false",
            }
        ).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<TimeProvider>(
                    new TestClock(new DateTime(Year, 9, 1, 9, 0, 0, DateTimeKind.Utc))
                )
            )
        );

    private static HttpClient FrontendClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            FrontendSecret
        );
        return client;
    }

    private static async Task<RegistrationEntity> SeedRegistration(
        WebApplicationFactory<Program> factory,
        RegistrationStatus status = RegistrationStatus.Approved,
        WasteProcessingKind kind = WasteProcessingKind.Exporter
    ) =>
        await factory
            .Services.GetRequiredService<RegistrationVersionStore>()
            .Versions.InsertAsync(
                new RegistrationEntity
                {
                    OrganisationId = ObjectId.GenerateNewId(),
                    ReExRegistrationId = RegistrationId,
                    RegistrationNumber = "R26EX0500030001PL",
                    Status = status,
                    WasteProcessingType = kind,
                    Material = MaterialType.Plastic,
                },
                Imported,
                cancellationToken: Ct
            );

    private static OverseasSiteEntity Ors(
        ObjectId registrationId,
        string orsId,
        string siteName,
        OverseasSiteStatus status = OverseasSiteStatus.Active,
        params InterimSiteEntry[] interimSites
    ) =>
        new()
        {
            RegistrationId = registrationId,
            OrsId = orsId,
            SiteName = siteName,
            FormattedAddress = $"{siteName} Street, Portugal",
            Address = new PostalAddress { Line1 = $"{siteName} Street", TownOrCity = "Porto", Country = "Portugal" },
            Country = "Portugal",
            IsEu = true,
            IsOecd = true,
            Coordinates = "41.1579, -8.6291",
            Contact = new ContactPerson { FullName = "Rui Costa", Email = "rui@example.com", Phone = "+351 1" },
            OperationCodes = ["R3"],
            BaselOecdCodes = ["B3011", "B3010"],
            RepatriatedLoads = "Returned to the UK.",
            ConditionsOfExport = true,
            Status = status,
            InterimSites = [.. interimSites],
        };

    private static InterimSiteEntry Interim(
        string siteNumber,
        string siteName,
        InterimSiteStatus status = InterimSiteStatus.Active
    ) =>
        new()
        {
            SiteNumber = siteNumber,
            SiteName = siteName,
            Address = new PostalAddress { Line1 = "1 Quay", TownOrCity = "Vigo", Country = "Spain" },
            Contact = new ContactPerson { FullName = "Ana Ruiz", Email = "ana@example.com", Phone = "+34 1" },
            OperationCodes = ["R13"],
            Status = status,
        };

    private static async Task SeedOrs(WebApplicationFactory<Program> factory, params OverseasSiteEntity[] sites)
    {
        var versions = factory.Services.GetRequiredService<OverseasSiteVersionStore>().Versions;
        foreach (var site in sites)
            await versions.InsertAsync(site, Imported, cancellationToken: Ct);
    }

    private static AccreditationChangeRequest Request(
        int day,
        params (string OrsId, ChangeItemStatus Status)[] items
    ) =>
        new()
        {
            OrganisationId = Org,
            ApplicationId = null,
            RegistrationId = RegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = Year,
            ChangeType = AccreditationChangeRequest.UpdateOverseasSiteChangeType,
            Status = items.Any(i => i.Status == ChangeItemStatus.InProgress)
                ? ChangeRequestStatus.InProgress
                : ChangeRequestStatus.Submitted,
            CreatedAt = new DateTime(Year, 3, day, 9, 0, 0, DateTimeKind.Utc),
            Sites =
            [
                .. items.Select(i => new ChangeRequestSite
                {
                    ChangeType = AccreditationChangeRequest.UpdateOverseasSiteChangeType,
                    SiteId = int.Parse(i.OrsId),
                    OrsId = i.OrsId,
                    SiteName = $"ORS {i.OrsId}",
                    Status = i.Status,
                }),
            ],
        };

    private static async Task SeedRequests(
        WebApplicationFactory<Program> factory,
        params AccreditationChangeRequest[] requests
    )
    {
        var store = factory.Services.GetRequiredService<IChangeRequestPersistence>();
        foreach (var request in requests)
            (await store.InsertAsync(request, Ct)).Should().BeTrue();
    }

    private static async Task<JsonElement> GetView(HttpClient client, string? path = null)
    {
        var response = await client.GetAsync(path ?? Path, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static List<string?> RequestIds(JsonElement view) =>
        view.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("requestId").GetString()).ToList();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForAnExporter_ListsItsActiveOrsRecordsAsRegisteredSitesWithInterimSites(bool expandEnabled)
    {
        using var factory = Factory(expandEnabled);
        using var client = FrontendClient(factory);
        var registration = await SeedRegistration(factory);
        await SeedOrs(
            factory,
            Ors(registration.EntityId, "012", "Porto Plasticos", interimSites: [Interim("001", "Vigo Store"), Interim("002", "Gone Store", InterimSiteStatus.Withdrawn)]),
            Ors(registration.EntityId, "003", "Braga Reciclagem"),
            Ors(registration.EntityId, "004", "Proposed Only", OverseasSiteStatus.Proposed),
            Ors(registration.EntityId, "005", "Discarded", OverseasSiteStatus.Discarded),
            Ors(ObjectId.GenerateNewId(), "006", "Another Registration's")
        );

        var view = await GetView(client);

        view.GetProperty("organisationId").GetString().Should().Be(Org);
        view.GetProperty("applicationId").ValueKind.Should().Be(JsonValueKind.Null);
        view.GetProperty("accreditationReference").ValueKind.Should().Be(JsonValueKind.Null);
        view.GetProperty("organisationName").GetString().Should().Be("Delta Green Ltd");
        view.GetProperty("registrationId").GetString().Should().Be(RegistrationId);
        view.GetProperty("registrationReference").GetString().Should().Be("R26EX0500030001PL");
        view.GetProperty("materialType").GetString().Should().Be("Plastic");
        view.GetProperty("year").GetInt32().Should().Be(Year);

        var sites = view.GetProperty("sites").EnumerateArray().ToList();
        sites.Select(s => (s.GetProperty("siteId").GetInt32(), s.GetProperty("orsId").GetString(), s.GetProperty("accreditationStatus").GetString()))
            .Should()
            .Equal((3, "003", "Registered"), (12, "012", "Registered"));
        var porto = sites[1].GetProperty("site");
        porto.GetProperty("siteId").GetInt32().Should().Be(12);
        porto.GetProperty("siteName").GetString().Should().Be("Porto Plasticos");
        porto.GetProperty("siteAddress").GetString().Should().Be("Porto Plasticos Street, Portugal");
        porto.GetProperty("addressLine1").GetString().Should().Be("Porto Plasticos Street");
        porto.GetProperty("townOrCity").GetString().Should().Be("Porto");
        porto.GetProperty("country").GetString().Should().Be("Portugal");
        porto.GetProperty("coordinates").GetString().Should().Be("41.1579, -8.6291");
        porto.GetProperty("contactName").GetString().Should().Be("Rui Costa");
        porto.GetProperty("contactEmail").GetString().Should().Be("rui@example.com");
        porto.GetProperty("contactPhone").GetString().Should().Be("+351 1");
        porto.GetProperty("operationCodes")[0].GetString().Should().Be("R3");
        porto.GetProperty("code1").GetString().Should().Be("B3011");
        porto.GetProperty("code2").GetString().Should().Be("B3010");
        porto.GetProperty("repatriatedLoads").GetString().Should().Be("Returned to the UK.");
        porto.GetProperty("conditionsOfExport").GetBoolean().Should().BeTrue();
        porto.GetProperty("selected").GetBoolean().Should().BeFalse();
        var interim = porto.GetProperty("interimSites").EnumerateArray().Should().ContainSingle().Subject;
        interim.GetProperty("siteNumber").GetString().Should().Be("001");
        interim.GetProperty("siteName").GetString().Should().Be("Vigo Store");
        interim.GetProperty("country").GetString().Should().Be("Spain");
        interim.GetProperty("contactName").GetString().Should().Be("Ana Ruiz");
        interim.GetProperty("operationCodes")[0].GetString().Should().Be("R13");
        sites[1].GetProperty("lockedFields").GetArrayLength().Should().Be(4);
        view.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);
        view.GetProperty("requests").GetArrayLength().Should().Be(0);
        view.GetProperty("pendingOrsIds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ForAReprocessor_HasNoSitesButListsItsRequests()
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);
        var registration = await SeedRegistration(factory, kind: WasteProcessingKind.Reprocessor);
        await SeedOrs(factory, Ors(registration.EntityId, "001", "Not An Exporter's"));
        var request = Request(1, ("001", ChangeItemStatus.Submitted));
        await SeedRequests(factory, request);

        var view = await GetView(client);

        view.GetProperty("sites").GetArrayLength().Should().Be(0);
        RequestIds(view).Should().Equal(request.RequestId);
    }

    [Fact]
    public async Task ListsOnlyThatRegistrationsCurrentYearRegistrationOnlyRequests_NewestFirst()
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);
        await SeedRegistration(factory);
        var older = Request(1, ("001", ChangeItemStatus.Approved));
        var newer = Request(2, ("002", ChangeItemStatus.Rejected));
        var accreditations = Request(3, ("001", ChangeItemStatus.Submitted));
        accreditations.ApplicationId = ObjectId.GenerateNewId().ToString();
        var lastYears = Request(4, ("001", ChangeItemStatus.Submitted));
        lastYears.Year = Year - 1;
        var otherRegistrations = Request(5, ("001", ChangeItemStatus.Submitted));
        otherRegistrations.RegistrationId = FakeOrganisationPersistence.Reg50002.ToString();
        var otherMaterials = Request(6, ("001", ChangeItemStatus.Submitted));
        otherMaterials.MaterialType = MaterialType.Glass;
        var otherOrgs = Request(7, ("001", ChangeItemStatus.Submitted));
        otherOrgs.OrganisationId = "50011";
        await SeedRequests(factory, older, newer, accreditations, lastYears, otherRegistrations, otherMaterials, otherOrgs);

        var view = await GetView(client);

        RequestIds(view).Should().Equal(newer.RequestId, older.RequestId);
    }

    // The same active-item rule as the accreditation view: an item not yet decided holds its site; granted,
    // refused and withdrawn ones do not. A registration-only draft is the active request.
    [Fact]
    public async Task PendingStatusesFollowTheActiveItemRule()
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);
        var registration = await SeedRegistration(factory);
        await SeedOrs(
            factory,
            Ors(registration.EntityId, "001", "Queried"),
            Ors(registration.EntityId, "002", "Granted"),
            Ors(registration.EntityId, "003", "Drafted"),
            Ors(registration.EntityId, "004", "Untouched")
        );
        var submitted = Request(1, ("001", ChangeItemStatus.Queried), ("002", ChangeItemStatus.Approved));
        var draft = Request(2, ("003", ChangeItemStatus.InProgress));
        await SeedRequests(factory, submitted, draft);

        var view = await GetView(client);

        view.GetProperty("sites")
            .EnumerateArray()
            .Select(s => (
                s.GetProperty("orsId").GetString(),
                s.GetProperty("pendingRequestId").GetString(),
                s.GetProperty("pendingRequestStatus").GetString()
            ))
            .Should()
            .Equal(
                ("001", submitted.RequestId, "Submitted"),
                ("002", null, null),
                ("003", draft.RequestId, "InProgress"),
                ("004", null, null)
            );
        view.GetProperty("pendingOrsIds").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("001", "003");
        view.GetProperty("activeRequest").GetProperty("requestId").GetString().Should().Be(draft.RequestId);
        var item = view.GetProperty("requests")[1].GetProperty("sites")[0];
        item.GetProperty("status").GetString().Should().Be("Queried");
        item.GetProperty("changeType").GetString().Should().Be("update-overseas-site");
    }

    [Fact]
    public async Task UnknownRegistration_Returns404()
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);

        (await client.GetAsync(Path, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(RegistrationStatus.Created)]
    [InlineData(RegistrationStatus.Rejected)]
    [InlineData(RegistrationStatus.Cancelled)]
    public async Task RegistrationNotApproved_Returns409(RegistrationStatus status)
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);
        await SeedRegistration(factory, status);

        (await client.GetAsync(Path, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // Fails closed: with no answer from ReEx on whether the organisation holds the registration there is no view,
    // 502 for an upstream failure and 503 for any other, as the accreditation application endpoints answer.
    [Theory]
    [InlineData(ReExErrorKind.ServerError, HttpStatusCode.BadGateway)]
    [InlineData(ReExErrorKind.Timeout, HttpStatusCode.BadGateway)]
    [InlineData(ReExErrorKind.AuthError, HttpStatusCode.ServiceUnavailable)]
    [InlineData(ReExErrorKind.ClientError, HttpStatusCode.ServiceUnavailable)]
    public async Task ReExFailing_FailsTheView(ReExErrorKind kind, HttpStatusCode expected)
    {
        using var baseFactory = Factory();
        using var factory = baseFactory.WithReExFailing(kind);
        using var client = FrontendClient(factory);
        await SeedRegistration(factory);

        (await client.GetAsync(Path, Ct)).StatusCode.Should().Be(expected);
    }

    // An ORS without a numeric ORS id cannot be keyed as a site, so it is left out, but not silently: a warning
    // says how many were left out for which registration, and nothing about the sites themselves.
    [Fact]
    public async Task AnOrsWithoutANumericOrsId_IsLeftOutWithAWarning()
    {
        var logs = new CapturingLoggerFactory();
        using var baseFactory = Factory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ILoggerFactory>(logs))
        );
        using var client = FrontendClient(factory);
        var registration = await SeedRegistration(factory);
        await SeedOrs(
            factory,
            Ors(registration.EntityId, "003", "Braga Reciclagem"),
            Ors(registration.EntityId, "ORS-X", "Unkeyable Site")
        );

        var view = await GetView(client);

        view.GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("orsId").GetString())
            .Should()
            .Equal("003");
        var warning = logs.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("numeric ORS id"))
            .Subject;
        warning.Message.Should().Contain("Left 1 ").And.Contain(RegistrationId).And.NotContain("Unkeyable");
    }

    // As the registration status endpoint: the URL's organisation must hold the registration.
    [Fact]
    public async Task ForAnOrganisationThatDoesNotHoldTheRegistration_Returns404()
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);
        await SeedRegistration(factory);

        (await client.GetAsync(Path.Replace($"/{Org}/", "/50011/"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("glass")]
    [InlineData("not-a-material")]
    public async Task ForAnotherMaterial_Returns404(string material)
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);
        await SeedRegistration(factory);

        (await client.GetAsync(Path.Replace("/plastic", $"/{material}"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MaterialIsCaseInsensitive()
    {
        using var factory = Factory();
        using var client = FrontendClient(factory);
        await SeedRegistration(factory);

        await GetView(client, Path.Replace("/plastic", "/Plastic"));
    }

    [Fact]
    public async Task InYearChangesFlagOff_Returns404()
    {
        using var factory = Factory(inYearChangesEnabled: false);
        using var client = FrontendClient(factory);

        (await client.GetAsync(Path, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithoutTheFrontendSecret_IsRejected()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        (await client.GetAsync(Path, Ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
