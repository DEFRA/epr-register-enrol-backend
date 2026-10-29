using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Versioning;
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
/// RA-644 B8c-B8e: the overseas site, interim site and number write paths with <c>DomainModel:DualWrite</c> on, end
/// to end over HTTP against a real replica set: the legacy write and the model writes commit together or not at
/// all, and side effects run only after the commit.
/// </summary>
public sealed class DualWriteEndpointTests : IAsyncDisposable
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

    // Knows no organisation, so the registration number's OrgId comes from the request.
    private readonly IReExApiAdapter _reEx = Substitute.For<IReExApiAdapter>();
    private WebApplicationFactory<Program> _factory;
    private HttpClient _client;

    public DualWriteEndpointTests(MongoIntegrationFixture fixture)
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
                services.AddSingleton(_reEx);
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

    // --- B8d: interim sites ---------------------------------------------------------------------------------------

    private static AddInterimSiteRequest InterimRequest(string name = "Quay") =>
        new()
        {
            Country = "Belgium",
            SiteName = name,
            AddressLine1 = "1 Quay",
            TownOrCity = "Antwerp",
            ContactName = "Ann",
            ContactEmail = "ann@example.com",
            ContactPhone = "0123 456 789",
            OperationCodes = ["R12"],
        };

    private static string InterimUrl(AccreditationApplicationModel application, string path = "") =>
        Url(application, $"/1/interim-sites{path}");

    private async Task<InterimSiteModel> AddInterimSiteAsync(
        AccreditationApplicationModel application,
        string name = "Quay"
    )
    {
        var response = await _client.PostAsJsonAsync(
            InterimUrl(application),
            InterimRequest(name),
            Ct
        );
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InterimSiteModel>(Ct))!;
    }

    private async Task<InterimSiteEntry> ModelInterimAsync(string siteNumber) =>
        Assert.Single(
            Assert.Single(await CurrentSitesAsync(), s => s.OrsId == "001").InterimSites,
            i => i.SiteNumber == siteNumber
        );

    [Fact]
    public async Task AddInterimSite_AddsAProposedInterimSiteToTheOrs()
    {
        var application = await ApplicationAsync(null, ReExSite());

        var added = await AddInterimSiteAsync(application);

        var interim = await ModelInterimAsync(added.SiteNumber);
        Assert.Equal(InterimSiteStatus.Proposed, interim.Status);
        Assert.Equal(application.Id, interim.ProposedByApplicationId);
        Assert.Equal("Quay", interim.SiteName);
        var entry = Assert.Single(
            (await CurrentApplicationAsync(application))!.ApplicationOverseasSites
        );
        Assert.Equal(added.SiteNumber, Assert.Single(entry.InterimSites).SiteNumber);
    }

    [Fact]
    public async Task AddInterimSite_SkipsANumberOnlyTheModelUses()
    {
        // The migration renumbers SN-0001 into the 3-digit pool, a number legacy has never seen.
        var site = ReExSite();
        site.InterimSites =
        [
            new InterimSiteModel
            {
                SiteId = 2,
                SiteNumber = "SN-0001",
                Country = "Belgium",
                SiteName = "Old",
                AddressLine1 = "2 Quay",
                TownOrCity = "Antwerp",
                ContactName = "Ann",
                ContactEmail = "ann@example.com",
                ContactPhone = "0123 456 789",
                OperationCodes = ["R12"],
            },
        ];
        var application = await ApplicationAsync(null, site);

        var added = await AddInterimSiteAsync(application);

        var numbers = Assert
            .Single(await CurrentSitesAsync())
            .InterimSites.Select(i => i.SiteNumber);
        Assert.Equal(2, numbers.Distinct().Count());
        Assert.Contains(added.SiteNumber, numbers);
    }

    [Fact]
    public async Task UpdateInterimSite_OwnProposal_WritesThroughToTheOrs()
    {
        var application = await ApplicationAsync(null, ReExSite());
        var added = await AddInterimSiteAsync(application);

        var response = await _client.PatchAsJsonAsync(
            InterimUrl(application, $"/{added.SiteId}"),
            InterimRequest("Dock"),
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var interim = await ModelInterimAsync(added.SiteNumber);
        Assert.Equal("Dock", interim.SiteName);
        Assert.Equal(InterimSiteStatus.Proposed, interim.Status);
    }

    [Fact]
    public async Task WithdrawThenRestoreInterimSite_OwnProposal_IsWithdrawnThenProposedAgain()
    {
        var application = await ApplicationAsync(null, ReExSite());
        var added = await AddInterimSiteAsync(application);

        var withdrawn = await _client.DeleteAsync(InterimUrl(application, $"/{added.SiteId}"), Ct);

        Assert.Equal(HttpStatusCode.NoContent, withdrawn.StatusCode);
        Assert.Equal(
            InterimSiteStatus.Withdrawn,
            (await ModelInterimAsync(added.SiteNumber)).Status
        );

        var restored = await _client.PostAsync(
            InterimUrl(application, $"/{added.SiteId}/restore"),
            null,
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        var interim = await ModelInterimAsync(added.SiteNumber);
        Assert.Equal(InterimSiteStatus.Proposed, interim.Status);
        Assert.Null(interim.RemovedAt);
    }

    [Fact]
    public async Task AddInterimSite_ModelWriteFails_RollsBackTheLegacyWriteAndSendsNoNotification()
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

        var response = await _client.PostAsJsonAsync(InterimUrl(application), InterimRequest(), Ct);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty((await LegacyAsync(application))!.OverseasSites!.Sites[0].InterimSites);
        await NotifySiteAdded(_caseWorking.DidNotReceive());
    }

    [Fact]
    public async Task AddInterimSite_Committed_NotifiesCaseManagementOnce()
    {
        var application = await ApplicationAsync(Guid.NewGuid(), ReExSite());

        await AddInterimSiteAsync(application);

        await NotifySiteAdded(_caseWorking.Received(1));
    }

    // --- B8e: registration and accreditation numbers --------------------------------------------------------------

    private static readonly object s_registrationNumberBody = new
    {
        nation = "England",
        orgId = 500027,
        year = 2027,
    };

    private async Task<AccreditationApplicationModel> NumberedApplicationAsync(
        ApplicationStatus status,
        int year,
        string? registrationReference = null
    )
    {
        var application = await ApplicationAsync();
        application.ApplicationStatus = status;
        application.Year = year;
        application.RegistrationReference = registrationReference;
        return (await Applications.UpdateAsync(application))!;
    }

    private static string NumberUrl(AccreditationApplicationModel application, string kind) =>
        $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}/{kind}-number";

    private async Task<RegistrationEntity> RegistrationOfAsync(
        AccreditationApplicationModel application
    ) =>
        (
            await Stores.Registrations.Versions.FindCurrentAsync(
                Builders<RegistrationEntity>.Filter.Eq(
                    r => r.EntityId,
                    (await CurrentApplicationAsync(application))!.RegistrationId!.Value
                ),
                cancellationToken: Ct
            )
        )!;

    [Fact]
    public async Task RegistrationNumber_FirstIssue_GoesToTheRegistrationAndTheInFlightApplications()
    {
        var approved = await NumberedApplicationAsync(ApplicationStatus.Approved, 2025);
        var withdrawn = await NumberedApplicationAsync(ApplicationStatus.Withdrawn, 2026);
        var inFlight = await NumberedApplicationAsync(ApplicationStatus.Submitted, 2026);
        var application = await NumberedApplicationAsync(ApplicationStatus.Started, 2027);

        var response = await _client.PostAsJsonAsync(
            NumberUrl(application, "registration"),
            s_registrationNumberBody,
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var number = (await LegacyAsync(application))!.RegistrationReference;
        Assert.NotNull(number);
        var registration = await RegistrationOfAsync(application);
        Assert.Equal(number, registration.RegistrationNumber);
        Assert.Equal(ChangeReason.RegistrationNumberIssued, registration.LastChange.Reason);
        Assert.Equal(number, (await LegacyAsync(inFlight))!.RegistrationReference);
        Assert.Null((await LegacyAsync(approved))!.RegistrationReference);
        Assert.Null((await LegacyAsync(withdrawn))!.RegistrationReference);
    }

    [Fact]
    public async Task RegistrationNumber_RegistrationAlreadyNumbered_IsReusedNotGenerated()
    {
        // The migration gives the Registration the approved application's number.
        await NumberedApplicationAsync(ApplicationStatus.Approved, 2026, "R26ER5000270001ST");
        var application = await NumberedApplicationAsync(ApplicationStatus.Started, 2027);

        // No nation, OrgId or year: generating would answer 400.
        var response = await _client.PostAsJsonAsync(
            NumberUrl(application, "registration"),
            new { },
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("R26ER5000270001ST", (await LegacyAsync(application))!.RegistrationReference);
        Assert.Equal(
            "R26ER5000270001ST",
            (await RegistrationOfAsync(application)).RegistrationNumber
        );
    }

    [Fact]
    public async Task RegistrationNumber_RegenerateAnIssuedNumber_IsRefusedAndChangesNothing()
    {
        var application = await NumberedApplicationAsync(
            ApplicationStatus.Started,
            2027,
            "R27ER5000270001ST"
        );

        var response = await _client.PostAsJsonAsync(
            NumberUrl(application, "registration"),
            new
            {
                nation = "England",
                orgId = 500027,
                year = 2027,
                regenerate = true,
            },
            Ct
        );

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal("R27ER5000270001ST", legacy.RegistrationReference);
        Assert.Empty(legacy.PreviousRegistrationNumbers);
        Assert.Equal(
            "R27ER5000270001ST",
            (await RegistrationOfAsync(application)).RegistrationNumber
        );
    }

    [Fact]
    public async Task RegistrationNumber_ModelWriteFails_RollsBackEveryLegacyWrite()
    {
        var sync = Substitute.For<IApplicationModelSync>();
        sync.SyncAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Throws(new InvalidOperationException("model write failed"));
        Restart(s_dualWriteOn, services => services.Replace(ServiceDescriptor.Singleton(sync)));
        var inFlight = await NumberedApplicationAsync(ApplicationStatus.Submitted, 2026);
        var application = await NumberedApplicationAsync(ApplicationStatus.Started, 2027);

        var response = await _client.PostAsJsonAsync(
            NumberUrl(application, "registration"),
            s_registrationNumberBody,
            Ct
        );

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null((await LegacyAsync(application))!.RegistrationReference);
        Assert.Null((await LegacyAsync(inFlight))!.RegistrationReference);
    }

    [Fact]
    public async Task AccreditationNumber_IsReservedOnTheApplicationVersion()
    {
        var application = await NumberedApplicationAsync(ApplicationStatus.Started, 2027);
        Assert.Equal(
            HttpStatusCode.OK,
            (
                await _client.PostAsJsonAsync(
                    NumberUrl(application, "registration"),
                    s_registrationNumberBody,
                    Ct
                )
            ).StatusCode
        );

        var response = await _client.PostAsync(NumberUrl(application, "accreditation"), null, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reference = (await LegacyAsync(application))!.AccreditationReference;
        Assert.NotNull(reference);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal(reference, entity.ReservedAccreditationNumber);
        Assert.Equal(ChangeReason.ApplicationEdited, entity.LastChange.Reason);
    }

    // --- B8f: rejected and withdrawn ------------------------------------------------------------------------------

    // Submitted with a site it added, and not migrated yet: the write under test is its first touch, so the
    // migration makes the added site a Proposed ORS before the handler runs.
    private async Task<(
        AccreditationApplicationModel Application,
        string OrsId
    )> SubmittedWithProposedSiteAsync()
    {
        var added = ReExSite(2, "002");
        added.SiteName = "Added Site";
        added.IsNewSite = true;
        var application = await ApplicationAsync(Guid.NewGuid(), ReExSite(), added);
        application.ApplicationStatus = ApplicationStatus.Submitted;
        return ((await Applications.UpdateAsync(application))!, "002");
    }

    private Task<HttpResponseMessage> StatusPushAsync(
        AccreditationApplicationModel application,
        string toStateId
    ) =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{application.CaseManagementWorkItemId}/status",
            new
            {
                toStateId,
                actionId = toStateId,
                occurredAt = DateTime.UtcNow,
            },
            Ct
        );

    private Task<HttpResponseMessage> WithdrawAsync(AccreditationApplicationModel application) =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}/withdraw",
            new { reason = "No longer needed" },
            Ct
        );

    private async Task<OverseasSiteEntity> CurrentSiteAsync(string orsId) =>
        Assert.Single(await CurrentSitesAsync(), s => s.OrsId == orsId);

    private void FailModelWrites()
    {
        var sync = Substitute.For<IApplicationModelSync>();
        sync.SyncAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Throws(new InvalidOperationException("model write failed"));
        Restart(s_dualWriteOn, services => services.Replace(ServiceDescriptor.Singleton(sync)));
    }

    [Fact]
    public async Task StatusPush_Rejected_DiscardsTheApplicationsProposedSite()
    {
        var (application, orsId) = await SubmittedWithProposedSiteAsync();

        var response = await StatusPushAsync(application, "rejected");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var ors = await CurrentSiteAsync(orsId);
        Assert.Equal(OverseasSiteStatus.Discarded, ors.Status);
        Assert.Equal(ChangeReason.ProposedSiteDiscarded, ors.LastChange.Reason);
        Assert.Equal(OverseasSiteStatus.Active, (await CurrentSiteAsync("001")).Status);
        Assert.Equal(
            ApplicationStatus.Rejected,
            (await CurrentApplicationAsync(application))!.Status
        );
    }

    [Fact]
    public async Task StatusPush_NotClosing_VersionsTheApplicationAndKeepsTheSiteProposed()
    {
        var (application, orsId) = await SubmittedWithProposedSiteAsync();

        var response = await StatusPushAsync(application, "duly-made");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OverseasSiteStatus.Proposed, (await CurrentSiteAsync(orsId)).Status);
        Assert.Equal(
            ApplicationStatus.DulyMade,
            (await CurrentApplicationAsync(application))!.Status
        );
    }

    [Fact]
    public async Task StatusPush_ModelWriteFails_RollsBackTheLegacyWrite()
    {
        FailModelWrites();
        var (application, _) = await SubmittedWithProposedSiteAsync();

        var response = await StatusPushAsync(application, "rejected");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(ApplicationStatus.Submitted, legacy.ApplicationStatus);
        Assert.Null(legacy.CaseManagementStatusUpdatedAt);
    }

    [Fact]
    public async Task StatusPush_UnknownWorkItem_IsNotFound()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{Guid.NewGuid()}/status",
            new
            {
                toStateId = "rejected",
                actionId = "rejected",
                occurredAt = DateTime.UtcNow,
            },
            Ct
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Withdraw_DiscardsTheApplicationsProposedSite()
    {
        var (application, orsId) = await SubmittedWithProposedSiteAsync();

        var response = await WithdrawAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OverseasSiteStatus.Discarded, (await CurrentSiteAsync(orsId)).Status);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal(ApplicationStatus.Withdrawn, entity.Status);
        Assert.Equal("No longer needed", entity.WithdrawalReason);
    }

    [Fact]
    public async Task Withdraw_ModelWriteFails_RollsBackTheLegacyWrite()
    {
        FailModelWrites();
        var (application, _) = await SubmittedWithProposedSiteAsync();

        var response = await WithdrawAsync(application);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(ApplicationStatus.Submitted, legacy.ApplicationStatus);
        Assert.Null(legacy.WithdrawalReason);
    }

    [Fact]
    public async Task Withdraw_ModelWriteFailsThenRetried_CompletesTheWithdrawal()
    {
        // Case Management withdraws on the first call and replays the withdrawal on the second, so the retry
        // records what the failed request rolled back.
        Restart(
            s_dualWriteOn,
            services =>
                services.Replace(
                    ServiceDescriptor.Singleton<IApplicationModelSync>(sp => new FailsOnceSync(
                        ActivatorUtilities.CreateInstance<ApplicationModelSync>(sp)
                    ))
                )
        );
        var (application, orsId) = await SubmittedWithProposedSiteAsync();

        var failed = await WithdrawAsync(application);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(
            ApplicationStatus.Submitted,
            (await LegacyAsync(application))!.ApplicationStatus
        );

        var retried = await WithdrawAsync(application);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(
            ApplicationStatus.Withdrawn,
            (await LegacyAsync(application))!.ApplicationStatus
        );
        Assert.Equal(OverseasSiteStatus.Discarded, (await CurrentSiteAsync(orsId)).Status);
        await _caseWorking
            .Received(2)
            .WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    private sealed class FailsOnceSync(IApplicationModelSync inner) : IApplicationModelSync
    {
        private int _calls;

        public Task SyncAsync(
            ObjectId applicationId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default
        ) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("model write failed")
                : inner.SyncAsync(applicationId, session, cancellationToken);
    }

    // --- B8g: an Application version on every remaining write path --------------------------------------------

    private static string AppUrl(AccreditationApplicationModel application, string path) =>
        $"/api/v1/accreditation-applications/{OrganisationId}/{application.Id}/{path}";

    // First touch migrates the application (version 1); the write itself is version 2.
    private async Task AssertWrittenAsVersion2Async(AccreditationApplicationModel application)
    {
        var entity = await CurrentApplicationAsync(application);
        Assert.NotNull(entity);
        Assert.Equal(2, entity.Version);
        Assert.Equal(ChangeReason.ApplicationEdited, entity.LastChange.Reason);
        Assert.Equal(
            (await LegacyAsync(application))!.DateLastEdited,
            entity.DateLastEdited,
            TimeSpan.FromMilliseconds(1)
        );
    }

    [Theory]
    [InlineData("prns", """{"plannedTonnageBand":"UpTo500"}""")]
    [InlineData("tonnage", """{"plannedTonnageBand":"UpTo500"}""")]
    [InlineData(
        "business-plan",
        """{"newInfrastructurePercent":10,"newInfrastructureDetail":"A new baler"}"""
    )]
    [InlineData("sampling-plan", """{"files":[]}""")]
    [InlineData("overseas-sites/1/bes-evidence", """{"doYouWantToUploadMoreEvidence":true}""")]
    public async Task SectionPatch_WritesAnApplicationVersion(string path, string body)
    {
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PatchAsync(
            AppUrl(application, path),
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            Ct
        );

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertWrittenAsVersion2Async(application);
    }

    [Fact]
    public async Task SectionPatch_ModelWriteFails_RollsBackTheLegacyWrite()
    {
        FailModelWrites();
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PatchAsJsonAsync(
            AppUrl(application, "business-plan"),
            new { newInfrastructurePercent = 10, newInfrastructureDetail = "A new baler" },
            Ct
        );

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Null((await LegacyAsync(application))!.BusinessPlan.NewInfrastructurePercent);
    }

    [Fact]
    public async Task DeleteFile_WritesAnApplicationVersion()
    {
        var application = await ApplicationAsync(null, ReExSite());
        application.SamplingPlan.Files =
        [
            new AccreditationApplicationFile
            {
                FileId = "file-1",
                Filename = "plan.pdf",
                ContentType = "application/pdf",
                UploadedByUserId = "user-1",
                S3Key = "plans/plan.pdf",
            },
        ];
        application = (await Applications.UpdateAsync(application))!;

        var response = await _client.DeleteAsync(AppUrl(application, "files/file-1"), Ct);

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Empty((await LegacyAsync(application))!.SamplingPlan.Files);
        await AssertWrittenAsVersion2Async(application);
    }

    [Fact]
    public async Task AddFile_WritesAnApplicationVersion()
    {
        var uploads = Substitute.For<IPendingUploadService>();
        uploads
            .GetStatusAsync("upload-1", Arg.Any<CancellationToken>())
            .Returns(
                new CdpStatusResponse
                {
                    UploadStatus = "ready",
                    ProcessingStatus = "validated",
                    Form = new CdpCallbackForm
                    {
                        File = new CdpCallbackFile
                        {
                            FileId = "file-1",
                            Filename = "plan.pdf",
                            S3Key = "plans/plan.pdf",
                            S3Bucket = "bucket",
                            ContentType = "application/pdf",
                        },
                    },
                }
            );
        Restart(s_dualWriteOn, services => services.Replace(ServiceDescriptor.Singleton(uploads)));
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PostAsJsonAsync(
            AppUrl(application, "files"),
            new { fileUploadId = "upload-1" },
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Single((await LegacyAsync(application))!.SamplingPlan.Files);
        await AssertWrittenAsVersion2Async(application);
    }

    private static IApplicationModelSync FailingSync()
    {
        var sync = Substitute.For<IApplicationModelSync>();
        sync.SyncAsync(
                Arg.Any<ObjectId>(),
                Arg.Any<IClientSessionHandle>(),
                Arg.Any<CancellationToken>()
            )
            .Throws(new InvalidOperationException("model write failed"));
        return sync;
    }

    private Task<HttpResponseMessage> PatchRecyclingOperationsAsync(
        AccreditationApplicationModel application
    ) =>
        _client.PatchAsJsonAsync(
            AppUrl(application, "overseas-sites/1/recycling-operations"),
            new { operationCodes = new[] { "R4", "R12" } },
            Ct
        );

    [Fact]
    public async Task PatchRecyclingOperations_WritesAnApplicationVersionAndAuditsAfterTheCommit()
    {
        var audit = Substitute.For<IRecyclingOperationsAuditPersistence>();
        Restart(s_dualWriteOn, services => services.Replace(ServiceDescriptor.Singleton(audit)));
        var application = await ApplicationAsync(null, ReExSite());

        var response = await PatchRecyclingOperationsAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal(2, entity.Version);
        Assert.Equal(
            ["R4", "R12"],
            Entry(entity).ProposedChanges!["operationCodes"].AsBsonArray.Select(c => c.AsString)
        );
        await audit
            .Received(1)
            .RecordAsync(Arg.Any<RecyclingOperationsAuditRecord>(), Arg.Any<CancellationToken>());
    }

    private static ApplicationOverseasSiteEntry Entry(ApplicationEntity entity) =>
        Assert.Single(entity.ApplicationOverseasSites);

    [Fact]
    public async Task PatchRecyclingOperations_ModelWriteFails_RollsBackAndWritesNoAudit()
    {
        var audit = Substitute.For<IRecyclingOperationsAuditPersistence>();
        var sync = FailingSync();
        Restart(
            s_dualWriteOn,
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton(audit));
                services.Replace(ServiceDescriptor.Singleton(sync));
            }
        );
        var application = await ApplicationAsync(null, ReExSite());

        var response = await PatchRecyclingOperationsAsync(application);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(
            ["R4"],
            (await LegacyAsync(application))!.OverseasSites!.Sites[0].OperationCodes
        );
        await audit
            .DidNotReceive()
            .RecordAsync(Arg.Any<RecyclingOperationsAuditRecord>(), Arg.Any<CancellationToken>());
    }

    private Task<HttpResponseMessage> QueryPushAsync(AccreditationApplicationModel application) =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{application.CaseManagementWorkItemId}/query",
            new
            {
                queryNote = "Please check the business plan.",
                sectionKeys = new[] { "business-plan" },
            },
            Ct
        );

    [Fact]
    public async Task QueryPush_WritesAnApplicationVersion()
    {
        var (application, _) = await SubmittedWithProposedSiteAsync();

        var response = await QueryPushAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal(ApplicationStatus.Queried, entity.Status);
        Assert.Equal(ChangeReason.ApplicationEdited, entity.LastChange.Reason);
    }

    [Fact]
    public async Task QueryPush_ModelWriteFails_RollsBackTheLegacyWrite()
    {
        FailModelWrites();
        var (application, _) = await SubmittedWithProposedSiteAsync();

        var response = await QueryPushAsync(application);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(ApplicationStatus.Submitted, legacy.ApplicationStatus);
        Assert.Null(legacy.Query);
    }

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
