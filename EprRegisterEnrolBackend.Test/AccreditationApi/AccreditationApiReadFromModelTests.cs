using System.Net;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.Test.Auth;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

/// <summary>
/// RA-705: with <c>DomainModel:ReadFromModel</c> on, the service-client accreditation API serves the granted
/// accreditation's content from the domain model. Each test changes legacy alone after the migration, so the response
/// shows which source it was read from. With the flag off the existing AccreditationApiEndpointTests apply unchanged.
/// </summary>
public sealed class AccreditationApiReadFromModelTests : IDisposable
{
    private const string ReExClientId = "epr-backend";
    private const string ReExSecret = "reex-integration-secret";
    private const int Org = 500027;
    private const string Registration = "R27EX5000270012AL";
    private const string OtherRegistration = "R27EX5000270099AL";
    private const string LegacyCollection = "accreditationApplications";
    private const string LegacyOnlySiteName = "Renamed In Legacy Only";

    private static string AccreditationPath(string registration = Registration) =>
        $"/v1/organisations/{Org}/registrations/{registration}/accreditations/2027";

    private readonly EphemeralMongoTestFactory _factory;
    private readonly HttpClient _client;

    // The same app with ReadFromModel off, over the same database, to compare the two sources.
    private readonly EphemeralMongoTestFactory _legacyFactory;
    private readonly HttpClient _legacyClient;

    public AccreditationApiReadFromModelTests(MongoIntegrationFixture fixture)
    {
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "accreditation_api_model",
            Environments.Production,
            Settings(readFromModel: true)
        );
        _client = _factory.CreateClient();
        _legacyFactory = new EphemeralMongoTestFactory(
            fixture,
            "accreditation_api_model",
            Environments.Production,
            Settings(readFromModel: false),
            _factory.DatabaseName
        );
        _legacyClient = _legacyFactory.CreateClient();
    }

    private static Dictionary<string, string?> Settings(bool readFromModel) =>
        new Dictionary<string, string?>
            {
                ["ServiceClients:Clients:0:Name"] = "reex",
                ["ServiceClients:Clients:0:ClientId"] = ReExClientId,
                ["AUTH_SHARED_SECRET:REEX"] = ReExSecret,
                ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = "integration-management-be-secret",
                ["AUTH_SHARED_SECRET:FRONTEND"] = "integration-frontend-secret",
                ["CaseWorking:UseStub"] = "true",
                ["DomainModel:ExpandEnabled"] = "true",
                ["DomainModel:DualWrite"] = "true",
                ["DomainModel:DualWriteSeed"] = "true",
                ["DomainModel:ReadFromModel"] = readFromModel ? "true" : "false",
            };

    public void Dispose()
    {
        _legacyClient.Dispose();
        _legacyFactory.Dispose();
        _client.Dispose();
        _factory.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OverseasSiteModel Site(int id, string orsId) =>
        new()
        {
            SiteId = id,
            OrsId = orsId,
            SiteName = $"Site {orsId}",
            AddressLine1 = $"{id} Hafenstrasse",
            TownOrCity = "Hamburg",
            Country = "Germany",
            Selected = true,
            ValidFrom = "2026-03-14T00:00:00.000Z",
        };

    private async Task<AccreditationApplicationModel> GrantedAsync(
        string registration = Registration
    )
    {
        var created = await _factory
            .Services.GetRequiredService<IAccreditationApplicationPersistence>()
            .CreateAsync(
                new AccreditationApplicationModel
                {
                    OrganisationId = "reex-object-id",
                    OrgId = Org,
                    OrganisationName = "Egger (UK) Limited",
                    Year = 2027,
                    MaterialType = MaterialType.Aluminium,
                    ApplicationStatus = ApplicationStatus.Approved,
                    RegistrationReference = registration,
                    RegistrationId = $"reex-registration-{registration}",
                    AccreditationReference = $"A{registration[1..]}",
                    IsExporter = true,
                    WasteProcessingType = "exporter",
                    Nation = Nation.England,
                    OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites = [Site(1, "001"), Site(2, "002")],
                    },
                }
            );
        return created ?? throw new InvalidOperationException("The legacy insert failed.");
    }

    private async Task MigrateAsync(AccreditationApplicationModel application)
    {
        var migrator = _factory.Services.GetRequiredService<IApplicationGroupMigrator>();
        await InsertRaceRetry.RunInTransactionAsync(
            _factory.Services.GetRequiredService<IMongoDbClientFactory>(),
            (session, token) => migrator.EnsureMigratedAsync(application.Id!.Value, session, token),
            _ => { },
            Ct
        );
    }

    // Migrated, then site 001 renamed in legacy alone (no sync runs for a raw edit).
    private async Task<AccreditationApplicationModel> MigratedThenSiteRenamedInLegacyAsync()
    {
        var application = await GrantedAsync();
        await MigrateAsync(application);
        await _factory
            .Services.GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>(LegacyCollection)
            .UpdateOneAsync(
                Builders<BsonDocument>.Filter.Eq("_id", application.Id!.Value),
                Builders<BsonDocument>.Update.Set(
                    "overseasSites.sites.0.siteName",
                    LegacyOnlySiteName
                ),
                cancellationToken: Ct
            );
        return application;
    }

    private Task<HttpResponseMessage> GetAsync(string path) =>
        _client.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, path, ReExClientId, ReExSecret),
            Ct
        );

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task GetOverseasSite_ServesTheModel()
    {
        await MigratedThenSiteRenamedInLegacyAsync();

        var site = await JsonAsync(await GetAsync($"{AccreditationPath()}/overseas-sites/001"));

        Assert.Equal("Site 001", site.GetProperty("siteName").GetString());
    }

    [Fact]
    public async Task ListOverseasSites_ServesTheModel()
    {
        await MigratedThenSiteRenamedInLegacyAsync();

        var page = await JsonAsync(await GetAsync($"{AccreditationPath()}/overseas-sites"));

        var names = page.GetProperty("items")
            .EnumerateArray()
            .Select(s => s.GetProperty("siteName").GetString())
            .ToList();
        Assert.Equal(["Site 001", "Site 002"], names);
    }

    [Fact]
    public async Task GetAccreditation_ServesTheModel()
    {
        await MigratedThenSiteRenamedInLegacyAsync();

        var accreditation = await JsonAsync(await GetAsync(AccreditationPath()));

        Assert.DoesNotContain(LegacyOnlySiteName, accreditation.GetRawText());
    }

    // Decided 2026-11-13: after the migration an accreditation missing from the model exists nowhere; no legacy fallback.
    [Fact]
    public async Task GetAccreditation_NotInTheModel_Answers404()
    {
        await GrantedAsync();

        var response = await GetAsync(AccreditationPath());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Search_LeavesOutAccreditationsNotInTheModel()
    {
        await MigratedThenSiteRenamedInLegacyAsync();
        await GrantedAsync(OtherRegistration);

        var page = await JsonAsync(await GetAsync($"/v1/accreditations?organisationNumber={Org}"));

        var registrations = page.GetProperty("items")
            .EnumerateArray()
            .Select(a => a.GetProperty("registrationNumber").GetString())
            .ToList();
        Assert.Equal([Registration], registrations);
    }

    private Task<HttpResponseMessage> GetLegacyAsync(string path) =>
        _legacyClient.SendAsync(
            ServiceClientSigner.Request(HttpMethod.Get, path, ReExClientId, ReExSecret),
            Ct
        );

    // Every API route answers field for field the same JSON from the model as from legacy, over the same data.
    [Theory]
    [InlineData("")]
    [InlineData("/overseas-sites")]
    [InlineData("/overseas-sites/001")]
    [InlineData("search")]
    public async Task Parity_SameJsonFromBothSources(string route)
    {
        var application = await GrantedAsync();
        await MigrateAsync(application);
        var path = route == "search"
            ? $"/v1/accreditations?organisationNumber={Org}"
            : AccreditationPath() + route;

        var fromLegacy = (await JsonAsync(await GetLegacyAsync(path))).GetRawText();
        var fromModel = (await JsonAsync(await GetAsync(path))).GetRawText();

        Assert.True(
            System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(fromLegacy),
                System.Text.Json.Nodes.JsonNode.Parse(fromModel)
            ),
            $"legacy {fromLegacy}\nmodel {fromModel}"
        );
    }
}
