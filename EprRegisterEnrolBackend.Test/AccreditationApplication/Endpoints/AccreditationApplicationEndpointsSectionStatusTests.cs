using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-496: covers the explicit-SectionStatus behaviour generalised across the Patch endpoints —
// the client's requested status (from the task-list's "save and come back later"/"save and
// continue" buttons) is honoured as an intent, gated by two rules every endpoint must apply:
// Completed is only accepted when the section is actually complete, and a Queried section ignores
// the request outright. Kept separate from AccreditationApplicationEndpointsPatchSectionsTests
// (which covers the pre-existing not-found/terminal/branch coverage for these same endpoints) to
// avoid merge conflicts.
public class AccreditationApplicationEndpointsSectionStatusTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsSectionStatusTests(
        AccreditationApplicationTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
    }

    private AccreditationApplicationModel SeedApplication(
        string orgId = "org-123",
        ApplicationStatus status = ApplicationStatus.Saved,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = orgId,
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = status,
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private async Task<AccreditationApplicationModel> Refetch(AccreditationApplicationModel app) =>
        (await _factory.FakePersistence.GetByIdAsync("org-123", app.Id!.Value.ToString()))!;

    // --- PatchPrns ---

    [Fact]
    public async Task PatchPrns_RequestedInProgress_WithPartialData_SetsInProgress()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchPrnsRequest
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo500,
            SectionStatus = SectionStatus.InProgress,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Prns.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public async Task PatchPrns_RequestedCompleted_WithCompleteData_SetsCompleted()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchPrnsRequest
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo500,
            Authorisers = [new PrnsAuthoriser { FullName = "A B", Email = "a@example.com" }],
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Prns.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task PatchPrns_RequestedCompleted_WithIncompleteData_Returns422AndLeavesStatusUnchanged()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchPrnsRequest
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo500,
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.Prns.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchPrns_RequestedQueried_Returns422AndLeavesStatusUnchanged()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchPrnsRequest { SectionStatus = SectionStatus.Queried };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.Prns.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchPrns_WhenQueried_IgnoresRequestedStatusAndStaysQueried()
    {
        Reset();
        var app = SeedApplication(configure: a => a.Prns.SectionStatus = SectionStatus.Queried);

        var request = new PatchPrnsRequest
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo500,
            Authorisers = [new PrnsAuthoriser { FullName = "A B", Email = "a@example.com" }],
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Prns.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    // --- PatchTonnage (shares the Prns model/status with PatchPrns) ---

    [Fact]
    public async Task PatchTonnage_RequestedCompleted_WithIncompleteData_Returns422()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchTonnageRequest { SectionStatus = SectionStatus.Completed };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/tonnage",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PatchTonnage_WhenQueried_IgnoresRequestedStatusAndStaysQueried()
    {
        Reset();
        var app = SeedApplication(configure: a => a.Prns.SectionStatus = SectionStatus.Queried);

        var request = new PatchTonnageRequest { SectionStatus = SectionStatus.InProgress };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/tonnage",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Prns.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    // --- PatchBusinessPlan ---

    private static PatchBusinessPlanRequest CompleteBusinessPlanRequest(SectionStatus? status) =>
        new()
        {
            NewInfrastructurePercent = 20,
            PriceSupportPercent = 20,
            BusinessCollectionsPercent = 20,
            CommunicationsPercent = 20,
            NewMarketsPercent = 10,
            NewUsesPercent = 5,
            OtherPercent = 5,
            IsPartialSave = true,
            SectionStatus = status,
        };

    [Fact]
    public async Task PatchBusinessPlan_RequestedInProgress_WithPartialData_SetsInProgress()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchBusinessPlanRequest
        {
            NewInfrastructurePercent = 20,
            IsPartialSave = true,
            SectionStatus = SectionStatus.InProgress,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BusinessPlan.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public async Task PatchBusinessPlan_RequestedCompleted_SumNot100_Returns422AndLeavesStatusUnchanged()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchBusinessPlanRequest
        {
            NewInfrastructurePercent = 20,
            IsPartialSave = true,
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.BusinessPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchBusinessPlan_RequestedCompleted_SumIs100_SetsCompleted()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            CompleteBusinessPlanRequest(SectionStatus.Completed),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task PatchBusinessPlan_RequestedQueried_Returns422AndLeavesStatusUnchanged()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchBusinessPlanRequest
        {
            IsPartialSave = true,
            SectionStatus = SectionStatus.Queried,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.BusinessPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchBusinessPlan_WhenQueried_IgnoresRequestedStatusAndStaysQueried()
    {
        Reset();
        var app = SeedApplication(
            configure: a => a.BusinessPlan.SectionStatus = SectionStatus.Queried
        );

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            CompleteBusinessPlanRequest(SectionStatus.Completed),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    // --- PatchSamplingPlan ---

    private static AccreditationApplicationFile CleanFile(string id) =>
        new()
        {
            FileId = id,
            Filename = $"{id}.pdf",
            ContentType = "application/pdf",
            UploadedByUserId = "user-1",
            ScanStatus = FileScanStatus.Clean,
            S3Key = $"sampling-plan/{id}",
        };

    private static AccreditationApplicationFile InfectedFile(string id) =>
        new()
        {
            FileId = id,
            Filename = $"{id}.pdf",
            ContentType = "application/pdf",
            UploadedByUserId = "user-1",
            ScanStatus = FileScanStatus.Infected,
            S3Key = $"sampling-plan/{id}",
        };

    [Fact]
    public async Task PatchSamplingPlan_RequestedInProgress_WithInfectedFile_SetsInProgress()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchSamplingPlanRequest
        {
            Files = [InfectedFile("file-1")],
            SectionStatus = SectionStatus.InProgress,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.SamplingPlan.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public async Task PatchSamplingPlan_RequestedCompleted_WithInfectedFile_Returns422()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchSamplingPlanRequest
        {
            Files = [InfectedFile("file-1")],
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.SamplingPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchSamplingPlan_RequestedCompleted_WithCleanFile_SetsCompleted()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchSamplingPlanRequest
        {
            Files = [CleanFile("file-1")],
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.SamplingPlan.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task PatchSamplingPlan_RequestedQueried_Returns422AndLeavesStatusUnchanged()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchSamplingPlanRequest { SectionStatus = SectionStatus.Queried };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.SamplingPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchSamplingPlan_WhenQueried_IgnoresRequestedStatusAndStaysQueried()
    {
        Reset();
        var app = SeedApplication(
            configure: a => a.SamplingPlan.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchSamplingPlanRequest
        {
            Files = [CleanFile("file-1")],
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.SamplingPlan.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    // --- PatchOverseasSites ---

    [Fact]
    public async Task PatchOverseasSites_RequestedInProgress_NoSiteSelected_SetsInProgress()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site", Selected = false }],
                }
        );

        var request = new PatchOverseasSitesRequest { SectionStatus = SectionStatus.InProgress };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OverseasSites!.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public async Task PatchOverseasSites_RequestedCompleted_NoSiteSelected_Returns422()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site", Selected = false }],
                }
        );

        var request = new PatchOverseasSitesRequest { SectionStatus = SectionStatus.Completed };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.OverseasSites!.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchOverseasSites_RequestedCompleted_WithSelectedSite_SetsCompleted()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site", Selected = true }],
                }
        );

        var request = new PatchOverseasSitesRequest { SectionStatus = SectionStatus.Completed };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OverseasSites!.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task PatchOverseasSites_RequestedQueried_Returns422AndLeavesStatusUnchanged()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site", Selected = true }],
                }
        );

        var request = new PatchOverseasSitesRequest { SectionStatus = SectionStatus.Queried };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var stored = await Refetch(app);
        stored.OverseasSites!.SectionStatus.Should().Be(SectionStatus.NotStarted);
    }

    [Fact]
    public async Task PatchOverseasSites_WhenQueried_IgnoresRequestedStatusAndStaysQueried()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site", Selected = true }],
                    SectionStatus = SectionStatus.Queried,
                }
        );

        var request = new PatchOverseasSitesRequest { SectionStatus = SectionStatus.Completed };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OverseasSites!.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    // --- PatchBesEvidenceSection: completeness gate (Queried-guard already covered by
    // AccreditationApplicationEndpointsBesEvidenceTests) ---

    [Fact]
    public async Task PatchBesEvidenceSection_RequestedCompleted_SiteRequiresEvidenceButNoneUploaded_Returns422()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site" }],
                }
        );

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_RequestedCompleted_RequiredSiteHasEvidence_SetsCompleted()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites =
                    [
                        new OverseasSiteModel
                        {
                            SiteId = 1,
                            SiteName = "Site",
                            BesEvidence = new BesEvidenceModel
                            {
                                BesEvidenceUploads =
                                [
                                    new BesEvidenceFileModel
                                    {
                                        FileId = "f1",
                                        Filename = "f1.pdf",
                                        S3Key = "bes-evidence/f1",
                                        ScanStatus = "Clean",
                                    },
                                ],
                            },
                        },
                    ],
                }
        );

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BesEvidence!.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_RequestedCompleted_SiteHasInfectedEvidence_Returns422()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites =
                    [
                        new OverseasSiteModel
                        {
                            SiteId = 1,
                            SiteName = "Site",
                            BesEvidence = new BesEvidenceModel
                            {
                                BesEvidenceUploads =
                                [
                                    new BesEvidenceFileModel
                                    {
                                        FileId = "f1",
                                        Filename = "f1.pdf",
                                        S3Key = "bes-evidence/f1",
                                        ScanStatus = "Infected",
                                    },
                                ],
                            },
                        },
                    ],
                }
        );

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_RequestedCompleted_SiteHasPendingEvidence_Returns422()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites =
                    [
                        new OverseasSiteModel
                        {
                            SiteId = 1,
                            SiteName = "Site",
                            BesEvidence = new BesEvidenceModel
                            {
                                BesEvidenceUploads =
                                [
                                    new BesEvidenceFileModel
                                    {
                                        FileId = "f1",
                                        Filename = "f1.pdf",
                                        S3Key = "bes-evidence/f1",
                                        ScanStatus = null,
                                    },
                                ],
                            },
                        },
                    ],
                }
        );

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_RequestedCompleted_OnlyEuSites_SetsCompletedWithoutEvidence()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site", IsEu = true }],
                }
        );

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BesEvidence!.SectionStatus.Should().Be(SectionStatus.Completed);
    }
}
