using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// Additional branch-coverage tests for the Patch* section endpoints on
// AccreditationApplicationEndpoints. Deliberately kept separate from
// AccreditationApplicationEndpointsTests.cs (which other work is touching in parallel) to avoid
// merge conflicts. See that file for the bulk of the existing validation-failure and
// terminal-conflict coverage for these same endpoints — this file targets what it does not yet
// cover: not-found, section-not-editable (where missing), the Saved->Started vs already-Started
// application-status branch, the Queried-skip-recompute branch, field-provided-vs-omitted branch
// pairs, and the UpdateAsync-returns-null -> Problem branch.
public class AccreditationApplicationEndpointsPatchSectionsTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsPatchSectionsTests(
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

    // Builds a client backed by a fully mocked IAccreditationApplicationPersistence whose
    // UpdateAsync always returns null, to reach the `updated is null ? Problem : Ok` branch that
    // the shared FakeAccreditationApplicationPersistence has no way to trigger (its UpdateAsync
    // only returns null when the id isn't already in the store, which GetByIdAsync would have
    // already 404'd on).
    private HttpClient CreateClientWhereUpdateFails(AccreditationApplicationModel app)
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));
        persistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(Task.FromResult<AccreditationApplicationModel?>(null));

        var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton(persistence)
            )
        );
        return factory.CreateClient();
    }

    // --- PatchPrns ---

    [Fact]
    public async Task PatchPrns_NotFound_Returns404()
    {
        Reset();
        var request = new PatchPrnsRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchPrns_WhenApplicationAlreadyStarted_LeavesApplicationStatusUnchanged()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new PatchPrnsRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
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
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchPrns_WhenUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplication();
        var client = CreateClientWhereUpdateFails(app);

        var request = new PatchPrnsRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // --- PatchTonnage ---

    [Fact]
    public async Task PatchTonnage_NotFound_Returns404()
    {
        Reset();
        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/tonnage",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchTonnage_WhenQueriedAndPrnsSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.BusinessPlan.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/tonnage",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchTonnage_WhenApplicationAlreadyStarted_LeavesApplicationStatusUnchanged()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
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
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchTonnage_WhenUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplication();
        var client = CreateClientWhereUpdateFails(app);

        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/tonnage",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // --- PatchBusinessPlan ---

    private static PatchBusinessPlanRequest AllFieldsBusinessPlanRequest() =>
        new()
        {
            NewInfrastructurePercent = 20,
            PriceSupportPercent = 20,
            BusinessCollectionsPercent = 20,
            CommunicationsPercent = 20,
            NewMarketsPercent = 10,
            NewUsesPercent = 10,
            NewInfrastructureDetail = "Infrastructure detail",
            PriceSupportDetail = "Price support detail",
            BusinessCollectionsDetail = "Business collections detail",
            CommunicationsDetail = "Communications detail",
            NewMarketsDetail = "New markets detail",
            NewUsesDetail = "New uses detail",
            IsPartialSave = true,
        };

    [Fact]
    public async Task PatchBusinessPlan_NotFound_Returns404()
    {
        Reset();
        var request = new PatchBusinessPlanRequest { IsPartialSave = true };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/business-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchBusinessPlan_AllFieldsProvided_UpdatesEveryFieldAndComputesCompleted()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            AllFieldsBusinessPlanRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BusinessPlan.NewInfrastructurePercent.Should().Be(20);
        body.BusinessPlan.PriceSupportPercent.Should().Be(20);
        body.BusinessPlan.BusinessCollectionsPercent.Should().Be(20);
        body.BusinessPlan.CommunicationsPercent.Should().Be(20);
        body.BusinessPlan.NewMarketsPercent.Should().Be(10);
        body.BusinessPlan.NewUsesPercent.Should().Be(10);
        body.BusinessPlan.NewInfrastructureDetail.Should().Be("Infrastructure detail");
        body.BusinessPlan.PriceSupportDetail.Should().Be("Price support detail");
        body.BusinessPlan.BusinessCollectionsDetail.Should().Be("Business collections detail");
        body.BusinessPlan.CommunicationsDetail.Should().Be("Communications detail");
        body.BusinessPlan.NewMarketsDetail.Should().Be("New markets detail");
        body.BusinessPlan.NewUsesDetail.Should().Be("New uses detail");
        // Percents sum to 100 -> ComputeBusinessPlan returns Completed.
        body.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Completed);
        body.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchBusinessPlan_NoFieldsProvided_LeavesBusinessPlanUnchanged()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Saved,
            configure: a =>
            {
                a.BusinessPlan.NewInfrastructurePercent = 5;
                a.BusinessPlan.NewInfrastructureDetail = "Existing detail";
            }
        );

        var request = new PatchBusinessPlanRequest { IsPartialSave = true };
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
        body!.BusinessPlan.NewInfrastructurePercent.Should().Be(5);
        body.BusinessPlan.NewInfrastructureDetail.Should().Be("Existing detail");
        body.BusinessPlan.PriceSupportPercent.Should().BeNull();
        body.BusinessPlan.PriceSupportDetail.Should().BeNull();
    }

    [Fact]
    public async Task PatchBusinessPlan_WhenQueriedAndBusinessPlanSectionIsQueried_SucceedsAndKeepsQueriedStatus()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.BusinessPlan.SectionStatus = SectionStatus.Queried
        );

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            AllFieldsBusinessPlanRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    [Fact]
    public async Task PatchBusinessPlan_WhenApplicationAlreadyStarted_LeavesApplicationStatusUnchanged()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            AllFieldsBusinessPlanRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchBusinessPlan_WhenUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplication();
        var client = CreateClientWhereUpdateFails(app);

        var response = await client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            AllFieldsBusinessPlanRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // --- PatchSamplingPlan ---

    [Fact]
    public async Task PatchSamplingPlan_NotFound_Returns404()
    {
        Reset();
        var request = new PatchSamplingPlanRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchSamplingPlan_FilesProvided_UpdatesFilesAndComputesCompleted()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

        var request = new PatchSamplingPlanRequest
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "file-1",
                    Filename = "plan.pdf",
                    ContentType = "application/pdf",
                    UploadedByUserId = "user-1",
                    ScanStatus = FileScanStatus.Clean,
                    S3Key = "sampling-plan/file-1",
                },
            ],
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
        body!.SamplingPlan.Files.Should().ContainSingle(f => f.FileId == "file-1");
        body.SamplingPlan.SectionStatus.Should().Be(SectionStatus.Completed);
        body.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchSamplingPlan_FilesOmitted_LeavesFilesUnchanged()
    {
        Reset();
        var existingFile = new AccreditationApplicationFile
        {
            FileId = "existing-file",
            Filename = "existing.pdf",
            ContentType = "application/pdf",
            UploadedByUserId = "user-1",
            ScanStatus = FileScanStatus.Clean,
            S3Key = "sampling-plan/existing-file",
        };
        var app = SeedApplication(
            status: ApplicationStatus.Saved,
            configure: a => a.SamplingPlan.Files = [existingFile]
        );

        var request = new PatchSamplingPlanRequest();
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
        body!.SamplingPlan.Files.Should().ContainSingle(f => f.FileId == "existing-file");
    }

    [Fact]
    public async Task PatchSamplingPlan_WhenQueriedAndSamplingPlanSectionIsQueried_SucceedsAndKeepsQueriedStatus()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.SamplingPlan.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchSamplingPlanRequest
        {
            Files =
            [
                new AccreditationApplicationFile
                {
                    FileId = "file-1",
                    Filename = "plan.pdf",
                    ContentType = "application/pdf",
                    UploadedByUserId = "user-1",
                    ScanStatus = FileScanStatus.Clean,
                    S3Key = "sampling-plan/file-1",
                },
            ],
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

    [Fact]
    public async Task PatchSamplingPlan_WhenApplicationAlreadyStarted_LeavesApplicationStatusUnchanged()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new PatchSamplingPlanRequest();
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
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchSamplingPlan_WhenUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplication();
        var client = CreateClientWhereUpdateFails(app);

        var request = new PatchSamplingPlanRequest();
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // --- PatchOverseasSites ---

    [Fact]
    public async Task PatchOverseasSites_NotFound_Returns404()
    {
        Reset();
        var request = new PatchOverseasSitesRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchOverseasSites_WhenApplicationAlreadyStarted_LeavesApplicationStatusUnchanged()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site" }],
                }
        );

        var request = new PatchOverseasSitesRequest();
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
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchOverseasSites_WhenUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site" }],
                }
        );
        var client = CreateClientWhereUpdateFails(app);

        var request = new PatchOverseasSitesRequest();
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
