using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

/// <summary>
/// RA-644 B8c: the overseas site write paths with <c>DomainModel:DualWrite</c> on, end to end over HTTP against a
/// real replica set: the legacy write and the model writes commit together or not at all, and side effects run
/// only after the commit.
/// </summary>
public sealed class OverseasSiteDualWriteEndpointTests : IAsyncDisposable
{
    private const string OrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";

    private static readonly Dictionary<string, string?> s_dualWriteOn = new()
    {
        ["DomainModel:ExpandEnabled"] = "true",
        ["DomainModel:DualWrite"] = "true",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MongoIntegrationFixture _fixture;
    private readonly ICaseWorkingApiAdapter _caseWorking = Substitute.For<ICaseWorkingApiAdapter>();
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;

    public OverseasSiteDualWriteEndpointTests(MongoIntegrationFixture fixture)
    {
        _fixture = fixture;
        (_factory, _client) = Start(s_dualWriteOn);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private (WebApplicationFactory<Program>, HttpClient) Start(
        Dictionary<string, string?> settings,
        Action<IServiceCollection>? configure = null
    )
    {
        var factory = new EphemeralMongoTestFactory(
            _fixture,
            "sitedualwrite",
            Environments.Development,
            settings
        ).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(_caseWorking);
                configure?.Invoke(services);
            })
        );
        return (factory, factory.CreateClient());
    }

    private void Restart(
        Dictionary<string, string?> settings,
        Action<IServiceCollection>? configure = null
    )
    {
        _client.Dispose();
        _factory.Dispose();
        (_factory, _client) = Start(settings, configure);
    }

    private IAccreditationApplicationPersistence Applications =>
        _factory.Services.GetRequiredService<IAccreditationApplicationPersistence>();

    // --- fixtures -------------------------------------------------------------------------------------------------

    // Steel, so R4 passes UpdateOverseasSite's material-type check.
    private static OverseasSiteModel ReExSite(int siteId = 1, string orsId = "001") =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = "ReEx Site",
            AddressLine1 = "1 Rue",
            TownOrCity = "Paris",
            Country = "France",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            OperationCodes = ["R4"],
            Code1 = "A1181",
            RepatriatedLoads = "Returned.",
        };

    private async Task<AccreditationApplicationModel> ApplicationAsync(
        Guid? workItemId = null,
        params OverseasSiteModel[] sites
    ) =>
        (
            await Applications.CreateAsync(
                new AccreditationApplicationModel
                {
                    Id = ObjectId.GenerateNewId(),
                    OrganisationId = OrganisationId,
                    OrganisationName = "Acme Recycling Ltd",
                    RegistrationId = ReExRegistrationId,
                    IsExporter = true,
                    Nation = Nation.England,
                    MaterialType = MaterialType.Steel,
                    Year = 2027,
                    ApplicationStatus = ApplicationStatus.Started,
                    CaseManagementWorkItemId = workItemId,
                    OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites = [.. sites],
                    },
                }
            )
        )!;

    private static string Url(AccreditationApplicationModel application, string path = "") =>
        $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}/overseas-sites{path}";

    private static AddOverseasSiteRequest AddRequest(string name = "Added Site") =>
        new()
        {
            SiteName = name,
            AddressLine1 = "Industriestrasse 42",
            TownOrCity = "Hamburg",
            Country = "Germany",
            ContactName = "Hans Muller",
            ContactEmail = "hans@example.de",
            OperationCodes = ["R4"],
            Code1 = "A1181",
            RepatriatedLoads = "Returned.",
        };

    private static PromoteOverseasSiteRequest EditRequest(string name) =>
        new()
        {
            SiteName = name,
            AddressLine1 = "1 Rue",
            TownOrCity = "Paris",
            Country = "France",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            OperationCodes = ["R4"],
            Code1 = "A1181",
            RepatriatedLoads = "Returned.",
        };

    private Task<AccreditationApplicationModel?> LegacyAsync(
        AccreditationApplicationModel application
    ) => Applications.GetByIdAsync(OrganisationId, application.Id.ToString()!);

    private ApplicationGroupStores Stores =>
        _factory.Services.GetRequiredService<ApplicationGroupStores>();

    private Task<List<OverseasSiteEntity>> CurrentSitesAsync() =>
        Stores.OverseasSites.Versions.FindAllCurrentAsync(
            FilterDefinition<OverseasSiteEntity>.Empty,
            cancellationToken: Ct
        );

    private async Task<ApplicationEntity?> CurrentApplicationAsync(
        AccreditationApplicationModel application
    ) =>
        await Stores.Applications.Versions.FindCurrentAsync(
            Builders<ApplicationEntity>.Filter.Eq(a => a.EntityId, application.Id!.Value),
            cancellationToken: Ct
        );

    // --- tests ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AddOverseasSite_FirstTouch_MigratesAndCreatesAProposedOrsWithTheLegacySite()
    {
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var legacySite = Assert.Single(
            (await LegacyAsync(application))!.OverseasSites!.Sites,
            s => s.SiteId != 1
        );
        var sites = await CurrentSitesAsync();
        Assert.Equal(OverseasSiteStatus.Active, Assert.Single(sites, s => s.OrsId == "001").Status);
        var added = Assert.Single(sites, s => s.OrsId == legacySite.OrsId);
        Assert.Equal(OverseasSiteStatus.Proposed, added.Status);
        Assert.Equal(application.Id, added.ProposedByApplicationId);
        Assert.Equal(added.EntityId, legacySite.OverseasSiteEntityId);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Contains(entity.ApplicationOverseasSites, e => e.OverseasSiteId == added.EntityId);
    }

    [Fact]
    public async Task AddOverseasSite_TwoAtOnce_GetDifferentOrsIdsAndBothOrssExist()
    {
        var application = await ApplicationAsync(null, ReExSite());

        var responses = await Task.WhenAll(
            _client.PostAsJsonAsync(Url(application), AddRequest("First"), Ct),
            _client.PostAsJsonAsync(Url(application), AddRequest("Second"), Ct)
        );

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var legacy = (await LegacyAsync(application))!.OverseasSites!.Sites;
        Assert.Equal(3, legacy.Count);
        Assert.Equal(3, legacy.Select(s => s.OrsId).Distinct().Count());
        var sites = await CurrentSitesAsync();
        Assert.Equal(legacy.Select(s => s.OrsId).Order(), sites.Select(s => s.OrsId).Order());
        Assert.Equal(
            3,
            (await CurrentApplicationAsync(application))!.ApplicationOverseasSites.Count
        );
    }

    [Fact]
    public async Task UpdateOverseasSite_OwnProposedSite_WritesThroughToTheOrs()
    {
        var application = await ApplicationAsync(null, ReExSite());
        var added = await (
            await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct)
        ).Content.ReadFromJsonAsync<OverseasSiteModel>(Ct);

        var response = await _client.PatchAsJsonAsync(
            Url(application, $"/{added!.SiteId}"),
            EditRequest("Renamed"),
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ors = Assert.Single(await CurrentSitesAsync(), s => s.OrsId == added.OrsId);
        Assert.Equal("Renamed", ors.SiteName);
        Assert.Equal(OverseasSiteStatus.Proposed, ors.Status);
    }

    [Fact]
    public async Task PromoteThenRevert_ReExSite_HoldsTheEditAsProposedChangesThenClearsIt()
    {
        var application = await ApplicationAsync(null, ReExSite());

        var promoted = await _client.PostAsJsonAsync(
            Url(application, "/1/promote"),
            EditRequest("Promoted"),
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);
        var ors = Assert.Single(await CurrentSitesAsync());
        Assert.Equal("ReEx Site", ors.SiteName);
        var entry = Assert.Single(
            (await CurrentApplicationAsync(application))!.ApplicationOverseasSites
        );
        Assert.Equal("Promoted", entry.ProposedChanges!["siteName"].AsString);
        Assert.True(entry.Selected);

        var reverted = await _client.PostAsync(Url(application, "/1/revert"), null, Ct);

        Assert.Equal(HttpStatusCode.OK, reverted.StatusCode);
        entry = Assert.Single(
            (await CurrentApplicationAsync(application))!.ApplicationOverseasSites
        );
        Assert.Null(entry.ProposedChanges);
        Assert.False(entry.Selected);
    }

    [Fact]
    public async Task PatchOverseasSites_OwnProposedSiteRenamed_WritesThroughToTheOrs()
    {
        var application = await ApplicationAsync(null, ReExSite());
        var added = await (
            await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct)
        ).Content.ReadFromJsonAsync<OverseasSiteModel>(Ct);
        var sites = (await LegacyAsync(application))!.OverseasSites!.Sites;
        sites.Single(s => s.SiteId == added!.SiteId).SiteName = "Patched";

        var response = await _client.PatchAsJsonAsync(Url(application), new { sites }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "Patched",
            Assert.Single(await CurrentSitesAsync(), s => s.OrsId == added!.OrsId).SiteName
        );
    }

    [Fact]
    public async Task AddOverseasSite_ModelWriteFails_RollsBackTheLegacyWriteAndSendsNoNotification()
    {
        var sync = Substitute.For<IApplicationModelSync>();
        sync.SyncAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Throws(new InvalidOperationException("model write failed"));
        Restart(s_dualWriteOn, services => services.Replace(ServiceDescriptor.Singleton(sync)));
        var application = await ApplicationAsync(Guid.NewGuid(), ReExSite());

        var response = await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Single((await LegacyAsync(application))!.OverseasSites!.Sites);
        Assert.Null((await LegacyAsync(application))!.RegistrationEntityId);
        Assert.Empty(await CurrentSitesAsync());
        await NotifySiteAdded(_caseWorking.DidNotReceive());
    }

    [Fact]
    public async Task AddOverseasSite_Committed_NotifiesCaseManagementOnce()
    {
        var application = await ApplicationAsync(Guid.NewGuid(), ReExSite());

        var response = await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await NotifySiteAdded(_caseWorking.Received(1));
    }

    private static Task NotifySiteAdded(ICaseWorkingApiAdapter adapter) =>
        adapter.NotifySiteAddedAsync(
            Arg.Any<AccreditationApplicationModel>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>()
        );

    [Fact]
    public async Task AddOverseasSite_DualWriteOff_WritesNoModel()
    {
        Restart(new() { ["DomainModel:ExpandEnabled"] = "true" });
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PostAsJsonAsync(Url(application), AddRequest(), Ct);

        // Neither ensure-migrated nor the sync ran: nothing stamped the model's keys on the legacy document.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(2, legacy.OverseasSites!.Sites.Count);
        Assert.Null(legacy.RegistrationEntityId);
        Assert.All(legacy.OverseasSites.Sites, s => Assert.Null(s.OverseasSiteEntityId));
    }
}
