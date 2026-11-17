using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.DomainModel.Reconciliation;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

/// <summary>
/// RA-495 task 007: Case Management's <c>approved</c> push for an add-ORS item grants the site in one dual-write
/// transaction, end to end over HTTP against a real replica set: the item, the legacy accreditation application's
/// site and the domain model's Accreditation and ORS commit together or not at all. Prior art: DualWriteEndpointTests.
/// </summary>
public sealed class ChangeRequestGrantDualWriteTests : IAsyncDisposable
{
    private const string OrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";
    private const string GrantedOrsId = "002";
    private static readonly DateTime SubmittedAt = new(2026, 4, 30, 9, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime Watermark = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly Switches _switches = new();
    private readonly Guid _workItemId = Guid.NewGuid();

    public ChangeRequestGrantDualWriteTests(MongoIntegrationFixture fixture)
    {
        var caseWorking = Substitute.For<ICaseWorkingApiAdapter>();
        var reEx = Substitute.For<IReExApiAdapter>();
        _factory = new EphemeralMongoTestFactory(
            fixture,
            "changegrant",
            Environments.Development,
            new Dictionary<string, string?>
            {
                ["DomainModel:ExpandEnabled"] = "true",
                ["DomainModel:DualWrite"] = "true",
                [FeatureFlags.InYearChangesEnabledKey] = "true",
            }
        ).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(caseWorking);
                services.AddSingleton(reEx);
                services.Replace(
                    ServiceDescriptor.Singleton<IApplicationModelSync>(sp => new SwitchableSync(
                        ActivatorUtilities.CreateInstance<ApplicationModelSync>(sp),
                        _switches
                    ))
                );
                services.Replace(
                    ServiceDescriptor.Singleton<IChangeRequestPersistence>(sp => new SwitchableRequests(
                        ActivatorUtilities.CreateInstance<ChangeRequestPersistence>(sp),
                        _switches
                    ))
                );
            })
        );
        _client = _factory.CreateClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // --- fixtures -------------------------------------------------------------------------------------------------

    private IAccreditationApplicationPersistence Applications =>
        _factory.Services.GetRequiredService<IAccreditationApplicationPersistence>();

    private IChangeRequestPersistence Requests =>
        _factory.Services.GetRequiredService<IChangeRequestPersistence>();

    private ApplicationGroupStores Stores => _factory.Services.GetRequiredService<ApplicationGroupStores>();

    private static OverseasSiteModel Site(int siteId, string orsId, bool selected) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = $"Site {orsId}",
            AddressLine1 = "1 Rue",
            TownOrCity = "Paris",
            Country = "France",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            OperationCodes = ["R4"],
            Code1 = "A1181",
            RepatriatedLoads = "Returned.",
            Selected = selected,
        };

    // The operator's granted accreditation: site 001 accredited, site 002 registered and not selected, as ReEx
    // leaves a site that is registered but not on the accreditation.
    private async Task<AccreditationApplicationModel> AccreditationAsync(OverseasSiteModel? registeredSite = null)
    {
        var created = await Applications.CreateAsync(
            new AccreditationApplicationModel
            {
                Id = ObjectId.GenerateNewId(),
                OrganisationId = OrganisationId,
                OrganisationName = "Acme Recycling Ltd",
                RegistrationId = ReExRegistrationId,
                IsExporter = true,
                Nation = Nation.England,
                MaterialType = MaterialType.Steel,
                Year = 2026,
                ApplicationStatus = ApplicationStatus.Approved,
                RegistrationReference = "R26ER5000270001ST",
                AccreditationReference = "A26ER5000270001ST",
                OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites =
                    [
                        Site(1, "001", selected: true),
                        registeredSite ?? Site(2, GrantedOrsId, selected: false),
                    ],
                },
            }
        );
        return created!;
    }

    private async Task SubmittedRequestAsync(
        AccreditationApplicationModel accreditation,
        ChangeRequestSiteAnswers? answers = null,
        BesEvidenceModel? besEvidence = null
    )
    {
        var inserted = await Requests.InsertAsync(
            new AccreditationChangeRequest
            {
                OrganisationId = OrganisationId,
                ApplicationId = accreditation.Id.ToString(),
                RegistrationId = ReExRegistrationId,
                MaterialType = MaterialType.Steel,
                Year = 2026,
                Status = ChangeRequestStatus.Submitted,
                SubmittedAt = SubmittedAt,
                Sites =
                [
                    new ChangeRequestSite
                    {
                        SiteId = 2,
                        OrsId = GrantedOrsId,
                        SiteName = "Site 002",
                        Status = ChangeItemStatus.DulyMade,
                        CaseManagementWorkItemId = _workItemId,
                        CaseManagementStatusUpdatedAt = Watermark,
                        Answers = answers ?? new ChangeRequestSiteAnswers(),
                        BesEvidence = besEvidence,
                    },
                ],
            },
            Ct
        );
        Assert.True(inserted);
    }

    private async Task<AccreditationApplicationModel> SubmittedAsync()
    {
        var accreditation = await AccreditationAsync();
        await SubmittedRequestAsync(accreditation);
        return accreditation;
    }

    // Migrates the application's group as any earlier write would, so a test can see what the grant itself changes.
    private async Task MigrateAsync(AccreditationApplicationModel application)
    {
        var result = await _factory
            .Services.GetRequiredService<IDualWriteUnitOfWork>()
            .RunAsync(
                "Test",
                application.Id.ToString()!,
                (_, _) => Task.FromResult(Results.Ok()),
                Ct
            );
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
    }

    private Task<HttpResponseMessage> PushAsync(string toStateId, DateTime occurredAt) =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-changes/case-management/{_workItemId}/status",
            new
            {
                toStateId,
                toStateDisplayName = toStateId,
                actionId = toStateId,
                actionDisplayName = toStateId,
                occurredAt,
            },
            Ct
        );

    private async Task<ChangeRequestSite> ItemAsync() =>
        (await Requests.GetByCaseManagementWorkItemIdAsync(_workItemId, Ct))!.Sites.Single();

    private async Task<OverseasSiteModel> LegacySiteAsync(AccreditationApplicationModel application, int siteId) =>
        (await Applications.GetByIdAsync(OrganisationId, application.Id.ToString()!))!.OverseasSites!.Sites.Single(s =>
            s.SiteId == siteId
        );

    private async Task<AccreditationEntity> AccreditationEntityAsync(AccreditationApplicationModel application) =>
        (
            await Stores.Accreditations.Versions.FindCurrentAsync(
                Builders<AccreditationEntity>.Filter.Eq(a => a.ApplicationId, application.Id),
                cancellationToken: Ct
            )
        )!;

    private async Task<OverseasSiteEntity> OrsAsync(string orsId) =>
        Assert.Single(
            await Stores.OverseasSites.Versions.FindAllCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(s => s.OrsId, orsId),
                cancellationToken: Ct
            )
        );

    private async Task<ApplicationEntity> ApplicationEntityAsync(AccreditationApplicationModel application) =>
        (
            await Stores.Applications.Versions.FindCurrentAsync(
                Builders<ApplicationEntity>.Filter.Eq(a => a.EntityId, application.Id!.Value),
                cancellationToken: Ct
            )
        )!;

    // Nothing of the grant is anywhere: the item is still being assessed and the site still only registered.
    private async Task AssertNotGrantedAsync(AccreditationApplicationModel application)
    {
        var item = await ItemAsync();
        Assert.Equal(ChangeItemStatus.DulyMade, item.Status);
        Assert.Null(item.ValidFrom);
        Assert.Equal(Watermark, item.CaseManagementStatusUpdatedAt);
        var site = await LegacySiteAsync(application, 2);
        Assert.False(site.Selected);
        Assert.Null(site.ValidFrom);
        var accreditation = await AccreditationEntityAsync(application);
        Assert.Equal(["001"], accreditation.AccreditationOverseasSites.Select(e => e.OrsId));
        Assert.Null((await OrsAsync(GrantedOrsId)).FirstApprovedAt);
    }

    // --- tests ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approved_GrantsTheSiteOnTheItemTheLegacyApplicationAndTheModel()
    {
        var application = await SubmittedAsync();
        var occurredAt = Watermark.AddMinutes(5);

        var response = await PushAsync("approved", occurredAt);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await ItemAsync();
        Assert.Equal(ChangeItemStatus.Approved, item.Status);
        Assert.Equal(SubmittedAt, item.ValidFrom);
        Assert.Equal(occurredAt, item.CaseManagementStatusUpdatedAt);

        var site = await LegacySiteAsync(application, 2);
        Assert.True(site.Selected);
        Assert.Equal(SiteApprovalDate.Format(SubmittedAt), site.ValidFrom);
        Assert.Equal(2, (await Applications.GetByIdAsync(OrganisationId, application.Id.ToString()!))!.OverseasSites!.Sites.Count);

        var ors = await OrsAsync(GrantedOrsId);
        var accreditation = await AccreditationEntityAsync(application);
        Assert.Equal(["001", GrantedOrsId], accreditation.AccreditationOverseasSites.Select(e => e.OrsId));
        var entry = accreditation.AccreditationOverseasSites.Single(e => e.OrsId == GrantedOrsId);
        Assert.Equal(ors.EntityId, entry.OverseasSiteId);
        Assert.Equal(AccreditedSiteStatus.Approved, entry.Status);
        Assert.Equal(ChangeReason.ChangeRequestGranted, accreditation.LastChange.Reason);
        Assert.True(accreditation.Version > 1, "the grant is a new version of the existing Accreditation");

        Assert.Equal(SubmittedAt, ors.FirstApprovedAt);
        Assert.Equal(FirstApprovalSource.ThisService, ors.FirstApprovalSource);
        Assert.Equal(OverseasSiteStatus.Active, ors.Status);

        // The unit of work's sync brings the Application version in line with the legacy selection.
        var modelEntry = (await ApplicationEntityAsync(application))
            .AsAccreditation()
            .ApplicationOverseasSites.Single(e => e.OverseasSiteId == ors.EntityId);
        Assert.True(modelEntry.Selected);
    }

    // 3bb.29: a registered site with no country, coordinates, R code or conditions of export answer, which the
    // operator supplied on the request with BES evidence.
    private static OverseasSiteModel UnansweredSite() =>
        new()
        {
            SiteId = 2,
            OrsId = GrantedOrsId,
            SiteName = $"Site {GrantedOrsId}",
            AddressLine1 = "1 Road",
            TownOrCity = "Delhi",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            Code1 = "A1181",
            RepatriatedLoads = "Returned.",
            Selected = false,
        };

    private static readonly ChangeRequestSiteAnswers SuppliedAnswers = new()
    {
        Country = "India",
        Coordinates = "28.6139, 77.2090",
        OperationCodes = ["R3"],
        ConditionsOfExport = false,
    };

    private static BesEvidenceModel SuppliedBesEvidence() =>
        new()
        {
            DoYouWantToUploadMoreEvidence = false,
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "bes-1",
                    Filename = "bes-1.pdf",
                    ContentType = "application/pdf",
                    ScanStatus = "Clean",
                    BesEvidenceValidFromDate = "2026-01-01",
                    BesEvidenceExpiryDate = "2027-01-01",
                    S3Key = "bes/bes-1",
                    S3Bucket = "bes-bucket",
                },
            ],
        };

    private async Task<AccreditationApplicationModel> SubmittedWithSuppliedAnswersAsync()
    {
        var accreditation = await AccreditationAsync(UnansweredSite());
        await SubmittedRequestAsync(accreditation, SuppliedAnswers, SuppliedBesEvidence());
        return accreditation;
    }

    // The fields the reconciliation report found differing between legacy and the model, for the one application.
    private async Task<IReadOnlyDictionary<string, long>> ReconciliationDifferencesAsync()
    {
        var report = await new ApplicationReconciler(
            _factory.Services.GetRequiredService<IMongoDbClientFactory>(),
            Applications,
            new ApplicationReadModel(Stores),
            Stores.Conflicts,
            TimeProvider.System
        ).RunAsync(Ct);
        Assert.Equal(1, report.Applications);
        Assert.Equal(0, report.NotMigrated + report.Unreadable + report.AcceptedConflicts);
        return report.DifferencesByField;
    }

    // As the operator's own add-to-accreditation journey and BES upload write them, then the approval carries them to
    // the ORS and the Accreditation: legacy and model both, so reconciliation finds nothing new.
    [Fact]
    public async Task Approved_WritesTheSuppliedAnswersAndBesEvidenceToLegacyAndTheModel()
    {
        var application = await SubmittedWithSuppliedAnswersAsync();

        var response = await PushAsync("approved", Watermark.AddMinutes(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var site = await LegacySiteAsync(application, 2);
        Assert.Equal("India", site.Country);
        Assert.Equal("28.6139, 77.2090", site.Coordinates);
        Assert.Equal(["R3"], site.OperationCodes);
        Assert.False(site.ConditionsOfExport);
        Assert.Equal(["bes-1"], site.BesEvidence!.BesEvidenceUploads.Select(f => f.FileId));

        var ors = await OrsAsync(GrantedOrsId);
        Assert.Equal("India", ors.Country);
        Assert.Equal("India", ors.Address!.Country);
        Assert.False(ors.IsEu);
        Assert.False(ors.IsOecd);
        Assert.Equal("28.6139, 77.2090", ors.Coordinates);
        Assert.Equal(["R3"], ors.OperationCodes);
        Assert.False(ors.ConditionsOfExport);
        Assert.Equal(ChangeReason.ChangeRequestGranted, ors.LastChange.Reason);

        var entry = (await AccreditationEntityAsync(application)).AccreditationOverseasSites.Single(e =>
            e.OrsId == GrantedOrsId
        );
        var file = Assert.Single(entry.BesEvidence);
        Assert.Equal("bes-1", file.FileId);
        Assert.Equal("bes/bes-1", file.S3Key);
        var modelEntry = (await ApplicationEntityAsync(application))
            .AsAccreditation()
            .ApplicationOverseasSites.Single(e => e.OverseasSiteId == ors.EntityId);
        Assert.Equal(["bes-1"], modelEntry.BesEvidence!.BesEvidenceUploads.Select(f => f.FileId));

        // Only the granted site's validFrom, already known to differ (3bb.28).
        var differences = await ReconciliationDifferencesAsync();
        Assert.All(differences.Keys, path => Assert.EndsWith("validFrom", path));
    }

    [Fact]
    public async Task Approved_WithSuppliedAnswers_ApprovedAgainWritesNothing()
    {
        var application = await SubmittedWithSuppliedAnswersAsync();
        Assert.Equal(HttpStatusCode.OK, (await PushAsync("approved", Watermark.AddMinutes(5))).StatusCode);
        var ors = await OrsAsync(GrantedOrsId);
        var accreditation = await AccreditationEntityAsync(application);
        var legacy = await Applications.GetByIdAsync(OrganisationId, application.Id.ToString()!);

        Assert.Equal(HttpStatusCode.OK, (await PushAsync("approved", Watermark.AddMinutes(10))).StatusCode);

        Assert.Equal(ors.Version, (await OrsAsync(GrantedOrsId)).Version);
        Assert.Equal(accreditation.Version, (await AccreditationEntityAsync(application)).Version);
        var after = await Applications.GetByIdAsync(OrganisationId, application.Id.ToString()!);
        Assert.Equal(legacy!.Version, after!.Version);
        Assert.Single(after.OverseasSites!.Sites.Single(s => s.SiteId == 2).BesEvidence!.BesEvidenceUploads);
    }

    // A failure rolls the supplied answers back with the rest of the grant; the retry writes them once.
    [Fact]
    public async Task Approved_WithSuppliedAnswers_RolledBackThenRetried_WritesThemOnce()
    {
        var application = await SubmittedWithSuppliedAnswersAsync();
        await MigrateAsync(application);
        _switches.FailSync = true;

        Assert.Equal(
            HttpStatusCode.InternalServerError,
            (await PushAsync("approved", Watermark.AddMinutes(5))).StatusCode
        );
        var rolledBack = await LegacySiteAsync(application, 2);
        Assert.Null(rolledBack.Country);
        Assert.Null(rolledBack.BesEvidence);
        Assert.Null((await OrsAsync(GrantedOrsId)).Coordinates);

        _switches.FailSync = false;
        Assert.Equal(HttpStatusCode.OK, (await PushAsync("approved", Watermark.AddMinutes(6))).StatusCode);

        Assert.Single((await LegacySiteAsync(application, 2)).BesEvidence!.BesEvidenceUploads);
        Assert.Equal("28.6139, 77.2090", (await OrsAsync(GrantedOrsId)).Coordinates);
    }

    [Fact]
    public async Task Approved_OrsAlreadyFirstApproved_KeepsItsDate()
    {
        var application = await SubmittedAsync();
        await MigrateAsync(application);
        var earlier = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ors = await OrsAsync(GrantedOrsId);
        ors.RecordFirstApproval(earlier, FirstApprovalSource.ThisService);
        await Stores.OverseasSites.Versions.UpdateAsync(
            ors,
            ors.Version,
            new ChangeInfo { Reason = ChangeReason.ReExRefresh },
            cancellationToken: Ct
        );

        var response = await PushAsync("approved", Watermark.AddMinutes(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await OrsAsync(GrantedOrsId);
        Assert.Equal(earlier, after.FirstApprovedAt);
        Assert.Equal(ors.Version + 1, after.Version);
        Assert.Contains(
            (await AccreditationEntityAsync(application)).AccreditationOverseasSites,
            e => e.OrsId == GrantedOrsId
        );
    }

    // A failure after every grant write (the unit of work's own sync) rolls them all back, item included: Case
    // Management's confirm-contact push fails, and its retry then grants the site.
    [Fact]
    public async Task Approved_ModelSyncFails_RollsEverythingBackAndARetryGrants()
    {
        var application = await SubmittedAsync();
        await MigrateAsync(application);
        _switches.FailSync = true;

        var failed = await PushAsync("approved", Watermark.AddMinutes(5));

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await AssertNotGrantedAsync(application);

        _switches.FailSync = false;
        var retried = await PushAsync("approved", Watermark.AddMinutes(6));

        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(ChangeItemStatus.Approved, (await ItemAsync()).Status);
        Assert.True((await LegacySiteAsync(application, 2)).Selected);
        Assert.Contains(
            (await AccreditationEntityAsync(application)).AccreditationOverseasSites,
            e => e.OrsId == GrantedOrsId
        );
    }

    // The item is the last write: when it fails, the legacy and model writes before it are rolled back too.
    [Fact]
    public async Task Approved_ItemWriteFails_RollsTheLegacyAndModelWritesBack()
    {
        var application = await SubmittedAsync();
        await MigrateAsync(application);
        _switches.LoseItemUpdates = true;

        var response = await PushAsync("approved", Watermark.AddMinutes(5));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        _switches.LoseItemUpdates = false;
        await AssertNotGrantedAsync(application);
    }

    [Fact]
    public async Task ApprovedAgain_WritesNothing()
    {
        var application = await SubmittedAsync();
        Assert.Equal(HttpStatusCode.OK, (await PushAsync("approved", Watermark.AddMinutes(5))).StatusCode);
        var item = await ItemAsync();
        var accreditation = await AccreditationEntityAsync(application);
        var ors = await OrsAsync(GrantedOrsId);
        var entity = await ApplicationEntityAsync(application);
        var legacy = await Applications.GetByIdAsync(OrganisationId, application.Id.ToString()!);

        var response = await PushAsync("approved", Watermark.AddMinutes(10));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(item.CaseManagementStatusUpdatedAt, (await ItemAsync()).CaseManagementStatusUpdatedAt);
        Assert.Equal(accreditation.Version, (await AccreditationEntityAsync(application)).Version);
        Assert.Equal(ors.Version, (await OrsAsync(GrantedOrsId)).Version);
        Assert.Equal(entity.Version, (await ApplicationEntityAsync(application)).Version);
        Assert.Equal(
            legacy!.Version,
            (await Applications.GetByIdAsync(OrganisationId, application.Id.ToString()!))!.Version
        );
    }

    // A refusal changes no accreditation: the site stays registered.
    [Fact]
    public async Task Rejected_LeavesTheSiteRegistered()
    {
        var application = await SubmittedAsync();

        var response = await PushAsync("rejected", Watermark.AddMinutes(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await ItemAsync();
        Assert.Equal(ChangeItemStatus.Rejected, item.Status);
        Assert.Null(item.ValidFrom);
        Assert.False((await LegacySiteAsync(application, 2)).Selected);
        Assert.Equal(
            ["001"],
            (await AccreditationEntityAsync(application)).AccreditationOverseasSites.Select(e => e.OrsId)
        );
        Assert.Null((await OrsAsync(GrantedOrsId)).FirstApprovedAt);
    }

    [Fact]
    public async Task NonFinalPush_InTheUnitOfWork_SetsOnlyTheItem()
    {
        var application = await SubmittedAsync();

        var response = await PushAsync("updated", Watermark.AddMinutes(5));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ChangeItemStatus.Updated, (await ItemAsync()).Status);
        Assert.False((await LegacySiteAsync(application, 2)).Selected);
    }

    // --- switchable stand-ins -------------------------------------------------------------------------------------

    private sealed class Switches
    {
        public volatile bool FailSync;
        public volatile bool LoseItemUpdates;
    }

    private sealed class SwitchableSync(IApplicationModelSync inner, Switches switches) : IApplicationModelSync
    {
        public Task SyncAsync(
            ObjectId applicationId,
            IClientSessionHandle session,
            CancellationToken cancellationToken = default
        ) =>
            switches.FailSync
                ? throw new InvalidOperationException("model write failed")
                : inner.SyncAsync(applicationId, session, cancellationToken);
    }

    private sealed class SwitchableRequests(IChangeRequestPersistence inner, Switches switches)
        : IChangeRequestPersistence
    {
        public Task<AccreditationChangeRequest?> GetDraftAsync(
            string organisationId,
            string applicationId,
            CancellationToken ct = default
        ) => inner.GetDraftAsync(organisationId, applicationId, ct);

        public Task<List<AccreditationChangeRequest>> ListAsync(
            string organisationId,
            string applicationId,
            CancellationToken ct = default
        ) => inner.ListAsync(organisationId, applicationId, ct);

        public Task<List<AccreditationChangeRequest>> ListForRegistrationAsync(
            string organisationId,
            string registrationId,
            MaterialType materialType,
            int year,
            CancellationToken ct = default
        ) => inner.ListForRegistrationAsync(organisationId, registrationId, materialType, year, ct);

        public Task<AccreditationChangeRequest?> GetByCaseManagementWorkItemIdAsync(
            Guid workItemId,
            CancellationToken ct = default,
            IClientSessionHandle? session = null
        ) => inner.GetByCaseManagementWorkItemIdAsync(workItemId, ct, session);

        public Task<bool> InsertAsync(AccreditationChangeRequest request, CancellationToken ct = default) =>
            inner.InsertAsync(request, ct);

        public Task<bool> UpdateAsync(
            AccreditationChangeRequest request,
            CancellationToken ct = default,
            IClientSessionHandle? session = null
        ) => switches.LoseItemUpdates ? Task.FromResult(false) : inner.UpdateAsync(request, ct, session);

        public Task<bool> DeleteDraftAsync(AccreditationChangeRequest request, CancellationToken ct = default) =>
            inner.DeleteDraftAsync(request, ct);
    }
}
