using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.DomainModel.DualWrite;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Utils.Mongo;
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
        IApplicationModelSync sync = new FailingModelSync();
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
        IApplicationModelSync sync = new FailingModelSync();
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
        IApplicationModelSync sync = new FailingModelSync();
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

    private void FailModelWrites(Action<IServiceCollection>? configure = null)
    {
        IApplicationModelSync sync = new FailingModelSync();
        Restart(
            s_dualWriteOn,
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton(sync));
                configure?.Invoke(services);
            }
        );
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
        UseScannedUpload("file-1", "plan.pdf");
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

    // Restarts with one validated upload, "upload-1", ready to attach.
    private void UseScannedUpload(string fileId, string filename) =>
        Restart(
            s_dualWriteOn,
            services =>
                services.Replace(ServiceDescriptor.Singleton(ScannedUpload(fileId, filename)))
        );

    private static IPendingUploadService ScannedUpload(string fileId, string filename)
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
                            FileId = fileId,
                            Filename = filename,
                            S3Key = $"uploads/{filename}",
                            S3Bucket = "bucket",
                            ContentType = "application/pdf",
                        },
                    },
                }
            );
        return uploads;
    }

    // One ORS carrying two uploaded BES evidence files, "bes-1" and "bes-2": the last file cannot be deleted.
    private async Task<AccreditationApplicationModel> WithBesEvidenceFileAsync()
    {
        var application = await ApplicationAsync(null, ReExSite());
        application.OverseasSites!.Sites[0].BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "bes-1",
                    Filename = "permit.pdf",
                    ContentType = "application/pdf",
                    ScanStatus = "Clean",
                    S3Key = "bes/permit.pdf",
                },
                new BesEvidenceFileModel
                {
                    FileId = "bes-2",
                    Filename = "licence.pdf",
                    ContentType = "application/pdf",
                    ScanStatus = "Clean",
                    S3Key = "bes/licence.pdf",
                },
            ],
        };
        return (await Applications.UpdateAsync(application))!;
    }

    private async Task<List<BesEvidenceFileModel>> LegacyBesUploadsAsync(
        AccreditationApplicationModel application
    ) => (await LegacyAsync(application))!.OverseasSites!.Sites[0].BesEvidence!.BesEvidenceUploads;

    [Fact]
    public async Task AddBesEvidenceFile_WritesAnApplicationVersion()
    {
        UseScannedUpload("bes-1", "permit.pdf");
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PostAsJsonAsync(
            AppUrl(application, "overseas-sites/1/bes-evidence/files"),
            new { fileUploadId = "upload-1" },
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Single(await LegacyBesUploadsAsync(application));
        await AssertWrittenAsVersion2Async(application);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_WritesAnApplicationVersion()
    {
        var application = await WithBesEvidenceFileAsync();

        var response = await _client.DeleteAsync(
            AppUrl(application, "overseas-sites/1/bes-evidence/files/bes-1"),
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Equal("bes-2", Assert.Single(await LegacyBesUploadsAsync(application)).FileId);
        await AssertWrittenAsVersion2Async(application);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_ModelWriteFails_RollsBackTheLegacyWrite()
    {
        FailModelWrites();
        var application = await WithBesEvidenceFileAsync();

        var response = await _client.DeleteAsync(
            AppUrl(application, "overseas-sites/1/bes-evidence/files/bes-1"),
            Ct
        );

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(2, (await LegacyBesUploadsAsync(application)).Count);
    }

    [Fact]
    public async Task PatchBesEvidenceFile_WritesAnApplicationVersion()
    {
        var application = await WithBesEvidenceFileAsync();

        var response = await _client.PatchAsJsonAsync(
            AppUrl(application, "overseas-sites/1/bes-evidence/files/bes-1"),
            new { besEvidenceExpiryDate = "2028-01-31" },
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Equal(
            "2028-01-31",
            (await LegacyBesUploadsAsync(application))
                .Single(f => f.FileId == "bes-1")
                .BesEvidenceExpiryDate
        );
        await AssertWrittenAsVersion2Async(application);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_WritesAnApplicationVersion()
    {
        var application = await ApplicationAsync(null, ReExSite());

        var response = await _client.PatchAsJsonAsync(
            AppUrl(application, "bes-evidence"),
            new { sectionStatus = "InProgress" },
            Ct
        );

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Equal(
            SectionStatus.InProgress,
            (await LegacyAsync(application))!.BesEvidence!.SectionStatus
        );
        await AssertWrittenAsVersion2Async(application);
    }

    // --- rollback on every write route ----------------------------------------------------------------------------

    private static InterimSiteModel StoredInterim(int siteId, string siteNumber) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            Country = "Belgium",
            SiteName = "Quay",
            AddressLine1 = "1 Quay",
            TownOrCity = "Antwerp",
            ContactName = "Ann",
            ContactEmail = "ann@example.com",
            ContactPhone = "0123 456 789",
            OperationCodes = ["R12"],
            IsNewSite = true,
        };

    // One application every route below can write to: a ReEx site (1) with an interim site (1) and a withdrawn one
    // (2), a site it added (2), a ReEx site it promoted (3), a sampling-plan file and a registration number.
    private async Task<AccreditationApplicationModel> EveryRouteWritableAsync()
    {
        var reEx = ReExSite();
        var withdrawn = StoredInterim(2, "002");
        withdrawn.RemovedAt = DateTime.UtcNow;
        reEx.InterimSites = [StoredInterim(1, "001"), withdrawn];
        InterimSiteSync.SyncMirror(reEx);

        var added = ReExSite(2, "002");
        added.IsNewSite = true;
        added.SiteName = "Added Site";

        var promoted = ReExSite(3, "003");
        promoted.RegisteredNowAccredited = true;
        promoted.PreviousSites = [ReExSite(3, "003")];

        var application = await ApplicationAsync(null, reEx, added, promoted);
        application.RegistrationReference = "R27ER5000270001PL";
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
        return (await Applications.UpdateAsync(application))!;
    }

    private Task<HttpResponseMessage> WriteAsync(
        AccreditationApplicationModel application,
        string route
    ) =>
        route switch
        {
            "update-site" => _client.PatchAsJsonAsync(
                Url(application, "/2"),
                EditRequest("Renamed"),
                Ct
            ),
            "promote-site" => _client.PostAsJsonAsync(
                Url(application, "/1/promote"),
                EditRequest("Promoted"),
                Ct
            ),
            "revert-site" => _client.PostAsync(Url(application, "/3/revert"), null, Ct),
            "patch-sites" => PatchSitesAsync(application),
            "update-interim" => _client.PatchAsJsonAsync(
                InterimUrl(application, "/1"),
                InterimRequest("Dock"),
                Ct
            ),
            "withdraw-interim" => _client.DeleteAsync(InterimUrl(application, "/1"), Ct),
            "restore-interim" => _client.PostAsync(InterimUrl(application, "/2/restore"), null, Ct),
            "add-file" => _client.PostAsJsonAsync(
                AppUrl(application, "files"),
                new { fileUploadId = "upload-1" },
                Ct
            ),
            "delete-file" => _client.DeleteAsync(AppUrl(application, "files/file-1"), Ct),
            "accreditation-number" => _client.PostAsync(
                NumberUrl(application, "accreditation"),
                null,
                Ct
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };

    private async Task<HttpResponseMessage> PatchSitesAsync(
        AccreditationApplicationModel application
    )
    {
        var sites = (await LegacyAsync(application))!.OverseasSites!.Sites;
        sites.Single(s => s.SiteId == 2).SiteName = "Patched";
        return await _client.PatchAsJsonAsync(Url(application), new { sites }, Ct);
    }

    // Each route reaches its legacy write (a 4xx would fail here, not pass), and a model failure rolls it back:
    // a route that wrote without the transaction's session would leave the legacy document changed.
    [Theory]
    [InlineData("update-site")]
    [InlineData("promote-site")]
    [InlineData("revert-site")]
    [InlineData("patch-sites")]
    [InlineData("update-interim")]
    [InlineData("withdraw-interim")]
    [InlineData("restore-interim")]
    [InlineData("add-file")]
    [InlineData("delete-file")]
    [InlineData("accreditation-number")]
    public async Task WriteRoute_ModelWriteFails_RollsBackTheLegacyWrite(string route)
    {
        FailModelWrites(services =>
            services.Replace(ServiceDescriptor.Singleton(ScannedUpload("file-2", "extra.pdf")))
        );
        var application = await EveryRouteWritableAsync();

        var response = await WriteAsync(application, route);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(application.Version, legacy.Version);
        Assert.Equal(application.UpdatedAt, legacy.UpdatedAt, TimeSpan.FromMilliseconds(1));
    }

    private static IApplicationModelSync FailingSync()
    {
        IApplicationModelSync sync = new FailingModelSync();
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

    // A reprocessor with every section complete, ready to submit.
    private async Task<AccreditationApplicationModel> ReadyToSubmitAsync()
    {
        var application = await ApplicationAsync(Guid.NewGuid());
        application.IsExporter = false;
        application.OverseasSites = null;
        application.Prns.SectionStatus = SectionStatus.Completed;
        application.BusinessPlan.SectionStatus = SectionStatus.Completed;
        application.SamplingPlan.SectionStatus = SectionStatus.Completed;
        return (await Applications.UpdateAsync(application))!;
    }

    private Task<HttpResponseMessage> SubmitAsync(AccreditationApplicationModel application) =>
        _client.PostAsJsonAsync(
            AppUrl(application, "submit"),
            new
            {
                fullName = "Ann Smith",
                jobTitle = "Director",
                email = "ann@example.com",
                paymentReference = "PAY-1",
            },
            Ct
        );

    private void CaseManagementAcceptsSubmissions(Guid workItemId) =>
        _caseWorking
            .SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new CaseWorkingSubmissionResult("ACC-REF-1", workItemId));

    [Fact]
    public async Task Submit_WritesAnApplicationVersion()
    {
        var workItemId = Guid.NewGuid();
        CaseManagementAcceptsSubmissions(workItemId);
        var application = await ReadyToSubmitAsync();

        var response = await SubmitAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal(ApplicationStatus.Submitted, entity.Status);
        Assert.Equal("ACC-REF-1", entity.ApplicationReference);
        Assert.Equal(workItemId, entity.CaseManagementWorkItemId);
    }

    [Fact]
    public async Task Submit_ModelWriteFailsThenRetried_CompletesTheSubmission()
    {
        Restart(
            s_dualWriteOn,
            services =>
                services.Replace(
                    ServiceDescriptor.Singleton<IApplicationModelSync>(sp => new FailsOnceSync(
                        ActivatorUtilities.CreateInstance<ApplicationModelSync>(sp)
                    ))
                )
        );
        CaseManagementAcceptsSubmissions(Guid.NewGuid());
        var application = await ReadyToSubmitAsync();

        var failed = await SubmitAsync(application);

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(
            ApplicationStatus.Started,
            (await LegacyAsync(application))!.ApplicationStatus
        );

        var retried = await SubmitAsync(application);

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(
            ApplicationStatus.Submitted,
            (await CurrentApplicationAsync(application))!.Status
        );
    }

    [Fact]
    public async Task Submit_TransactionRunsTwice_SavesTheSubmission()
    {
        // The first attempt loses an insert race inside the transaction, so the unit of work runs the save again in
        // the same request.
        var taken = ObjectId.GenerateNewId();
        Restart(
            s_dualWriteOn,
            services =>
                services.Replace(
                    ServiceDescriptor.Singleton<IApplicationModelSync>(
                        sp => new LosesAnInsertRaceOnceSync(
                            ActivatorUtilities.CreateInstance<ApplicationModelSync>(sp),
                            RaceTarget(sp),
                            taken
                        )
                    )
                )
        );
        await RaceTarget(_factory.Services)
            .InsertOneAsync(new BsonDocument("_id", taken), cancellationToken: Ct);
        CaseManagementAcceptsSubmissions(Guid.NewGuid());
        var application = await ReadyToSubmitAsync();

        var response = await SubmitAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ApplicationStatus.Submitted,
            (await LegacyAsync(application))!.ApplicationStatus
        );
        Assert.Equal(
            ApplicationStatus.Submitted,
            (await CurrentApplicationAsync(application))!.Status
        );
    }

    [Fact]
    public async Task Submit_FirstTouchWithNoSites_KeepsTheLinkToTheRegistration()
    {
        // The migration stamps the Registration on the legacy document in the same transaction; the submission must
        // not write over it.
        CaseManagementAcceptsSubmissions(Guid.NewGuid());
        var application = await ReadyToSubmitAsync();

        var response = await SubmitAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registrationId = (await CurrentApplicationAsync(application))!.RegistrationId;
        Assert.NotNull(registrationId);
        Assert.Equal(registrationId, (await LegacyAsync(application))!.RegistrationEntityId);
    }

    private static IMongoCollection<BsonDocument> RaceTarget(IServiceProvider services) =>
        services
            .GetRequiredService<IMongoDbClientFactory>()
            .GetCollection<BsonDocument>("raceTarget");

    // Loses one insert race: the first sync inserts a key a rival has already committed.
    private sealed class LosesAnInsertRaceOnceSync(
        IApplicationModelSync inner,
        IMongoCollection<BsonDocument> raceTarget,
        ObjectId taken
    ) : IApplicationModelSync
    {
        private int _calls;

        public async Task SyncAsync(
            ObjectId applicationId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default
        )
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await raceTarget.InsertOneAsync(
                    session,
                    new BsonDocument("_id", taken),
                    cancellationToken: cancellationToken
                );
            }

            await inner.SyncAsync(applicationId, session, cancellationToken);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Submit_EditedDuringTheCaseManagementCall_IsNotSaved(bool dualWrite)
    {
        // An operator edit lands while Case Management handles the submit. Case Management holds the copy read
        // before the call, so the newer content must not be stored as submitted, with the flag on or off.
        if (!dualWrite)
            Restart(new() { ["DomainModel:ExpandEnabled"] = "true" });
        var application = await ReadyToSubmitAsync();
        _caseWorking
            .SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(async _ =>
            {
                var edited = (await LegacyAsync(application))!;
                edited.BusinessPlan.SectionStatus = SectionStatus.InProgress;
                await Applications.UpdateAsync(edited);
                return new CaseWorkingSubmissionResult("ACC-REF-1", Guid.NewGuid());
            });

        var response = await SubmitAsync(application);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(ApplicationStatus.Started, legacy.ApplicationStatus);
        Assert.Equal(SectionStatus.InProgress, legacy.BusinessPlan.SectionStatus);
    }

    // Queried on the business plan, as the Case Management service leaves it.
    private async Task<AccreditationApplicationModel> QueriedAsync()
    {
        var application = await ApplicationAsync(Guid.NewGuid(), ReExSite());
        application.ApplicationStatus = ApplicationStatus.Queried;
        application.BusinessPlan.SectionStatus = SectionStatus.Queried;
        application.Query = new AccreditationApplicationQuery
        {
            QueryNote = "Please check the business plan.",
            QueriedSectionKeys = ["business-plan"],
        };
        return (await Applications.UpdateAsync(application))!;
    }

    private Task<HttpResponseMessage> ResubmitAsync(AccreditationApplicationModel application)
    {
        _caseWorking
            .ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new ResumeFromQueryResult(true));
        return _client.PostAsJsonAsync(
            AppUrl(application, "resubmit"),
            new
            {
                fullName = "Ann Smith",
                email = "ann@example.com",
                role = "Director",
            },
            Ct
        );
    }

    [Fact]
    public async Task Resubmit_WritesAnApplicationVersion()
    {
        var application = await QueriedAsync();

        var response = await ResubmitAsync(application);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ApplicationStatus.Updated,
            (await CurrentApplicationAsync(application))!.Status
        );
    }

    [Fact]
    public async Task Resubmit_ModelWriteFails_RollsBackTheLegacyWrite()
    {
        FailModelWrites();
        var application = await QueriedAsync();

        var response = await ResubmitAsync(application);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var legacy = (await LegacyAsync(application))!;
        Assert.Equal(ApplicationStatus.Queried, legacy.ApplicationStatus);
        Assert.Equal(["business-plan"], legacy.Query!.QueriedSectionKeys);
    }

    // RA-519 under dual-write: an operator edit and a Case Management push on the same
    // application at once. Each runs in a transaction that re-reads on a write conflict, so
    // neither whole-document write can lose the other's change.
    [Fact]
    public async Task OperatorEditAndCaseManagementPushAtOnce_BothChangesSurvive()
    {
        for (var round = 0; round < 5; round++)
        {
            var application = await QueriedAsync();

            var responses = await Task.WhenAll(
                _client.PatchAsJsonAsync(
                    AppUrl(application, "business-plan"),
                    new { newInfrastructurePercent = 10, newInfrastructureDetail = "A new baler" },
                    Ct
                ),
                StatusPushAsync(application, "case-note-added")
            );

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var legacy = (await LegacyAsync(application))!;
            Assert.Equal(10, legacy.BusinessPlan.NewInfrastructurePercent);
            Assert.NotNull(legacy.CaseManagementStatusUpdatedAt);
            var entity = (await CurrentApplicationAsync(application))!;
            Assert.Equal(10, entity.BusinessPlan.NewInfrastructurePercent);
            Assert.NotNull(entity.CaseManagementStatusUpdatedAt);
        }
    }

    // --- B9: approval --------------------------------------------------------------------------------------------

    // Submitted with a site it added, and given its accreditation number, as the Case Management service does
    // before it approves.
    private async Task<(
        AccreditationApplicationModel Application,
        string OrsId
    )> ReadyToApproveAsync()
    {
        var (application, orsId) = await SubmittedWithProposedSiteAsync();
        application.AccreditationReference = "A27ER5000270001ST";
        return ((await Applications.UpdateAsync(application))!, orsId);
    }

    private Task<List<AccreditationEntity>> CurrentAccreditationsAsync() =>
        Stores.Accreditations.Versions.FindAllCurrentAsync(
            FilterDefinition<AccreditationEntity>.Empty,
            cancellationToken: Ct
        );

    [Fact]
    public async Task StatusPush_Approved_CreatesTheAccreditationAndActivatesTheSites()
    {
        var (application, orsId) = await ReadyToApproveAsync();

        var response = await StatusPushAsync(application, "approved");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accreditation = Assert.Single(await CurrentAccreditationsAsync());
        Assert.Equal(application.Id, accreditation.ApplicationId);
        Assert.Equal("A27ER5000270001ST", accreditation.AccreditationNumber);
        Assert.Equal(2, accreditation.AccreditationOverseasSites.Count);
        Assert.Equal(OverseasSiteStatus.Active, (await CurrentSiteAsync(orsId)).Status);
        var entity = (await CurrentApplicationAsync(application))!;
        Assert.Equal(ApplicationStatus.Approved, entity.Status);
        Assert.Equal(accreditation.EntityId, entity.AccreditationId);
        Assert.Equal(
            accreditation.EntityId,
            (await LegacyAsync(application))!.AccreditationEntityId
        );
    }

    [Fact]
    public async Task StatusPush_ApprovedAgain_CreatesNothingNew()
    {
        var (application, _) = await ReadyToApproveAsync();
        await StatusPushAsync(application, "approved");
        var accreditation = Assert.Single(await CurrentAccreditationsAsync());
        var entity = (await CurrentApplicationAsync(application))!;

        var response = await StatusPushAsync(application, "approved");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            accreditation.Version,
            Assert.Single(await CurrentAccreditationsAsync()).Version
        );
        Assert.Equal(entity.Version, (await CurrentApplicationAsync(application))!.Version);
    }

    [Fact]
    public async Task StatusPush_TwoApprovalsAtOnce_CreateOneAccreditation()
    {
        var (application, _) = await ReadyToApproveAsync();

        var responses = await Task.WhenAll(
            StatusPushAsync(application, "approved"),
            StatusPushAsync(application, "approved")
        );

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(
            application.Id,
            Assert.Single(await CurrentAccreditationsAsync()).ApplicationId
        );
    }

    [Fact]
    public async Task StatusPush_ApprovedWithoutAnAccreditationNumber_FailsAndChangesNothing()
    {
        var (application, orsId) = await SubmittedWithProposedSiteAsync();

        var response = await StatusPushAsync(application, "approved");

        // A rule, not a fault: a retry fails the same way, so it is a 409 naming the rule.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            nameof(DomainRule.AccreditationNumberMissing),
            await response.Content.ReadAsStringAsync(Ct)
        );
        Assert.Equal(
            ApplicationStatus.Submitted,
            (await LegacyAsync(application))!.ApplicationStatus
        );
        Assert.Empty(await CurrentAccreditationsAsync());
        Assert.Empty(await CurrentSitesAsync());
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
