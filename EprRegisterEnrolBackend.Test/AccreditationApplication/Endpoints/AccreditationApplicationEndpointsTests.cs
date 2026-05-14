using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationEndpointsTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
    }

    private AccreditationApplicationModel SeedApplication(
        string orgId = "org-123",
        ApplicationStatus status = ApplicationStatus.Saved,
        Action<AccreditationApplicationModel>? configure = null)
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = orgId,
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = status
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    // --- Seed ---

    [Fact]
    public async Task Seed_ValidRequest_Returns201WithApplication()
    {
        Reset();
        _factory.MockReExAdapter
            .GetAccreditationAsync(Arg.Any<string>(), Arg.Any<MaterialType>(), Arg.Any<int>())
            .Returns(Task.FromResult<ReExAccreditationDto?>(null));

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync("/api/v1/accreditation-applications/org-123/site-1/Steel/seed", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        body!.OrganisationId.Should().Be("org-123");
        body.MaterialType.Should().Be(MaterialType.Steel);
        body.ApplicationStatus.Should().Be(ApplicationStatus.Saved);
    }

    [Fact]
    public async Task Seed_WithPriorYearData_PrePopulatesFields()
    {
        Reset();
        _factory.MockReExAdapter
            .GetAccreditationAsync("org-123", MaterialType.Steel, 2025)
            .Returns(Task.FromResult<ReExAccreditationDto?>(new ReExAccreditationDto
            {
                AccreditationId = "reex-abc",
                OrganisationId = "org-123",
                MaterialType = MaterialType.Steel,
                Year = 2025,
                Prns = new ReExPrnsDto { PlannedTonnageBand = PlannedTonnageBand.UpTo1000 },
                BusinessPlan = new ReExBusinessPlanDto { NewInfrastructurePercent = 20 }
            }));

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync("/api/v1/accreditation-applications/org-123/site-1/Steel/seed", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        body!.SourceReExAccreditationId.Should().Be("reex-abc");
        body.SourceYear.Should().Be(2025);
        body.Prns.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo1000);
    }

    [Fact]
    public async Task Seed_InvalidYear_Returns400()
    {
        Reset();
        var request = new SeedRequest { Year = 2020 };
        var response = await _client.PostAsJsonAsync("/api/v1/accreditation-applications/org-123/site-1/Steel/seed", request,
            cancellationToken: TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Seed_InvalidMaterialType_Returns400()
    {
        Reset();
        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync("/api/v1/accreditation-applications/org-123/site-1/Unknown/seed", request,
            cancellationToken: TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- GetList ---

    [Fact]
    public async Task GetList_ReturnsApplicationsForOrg()
    {
        Reset();
        SeedApplication();

        var response = await _client.GetAsync("/api/v1/accreditation-applications/org-123",
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AccreditationApplicationModel>>(JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        body.Should().HaveCount(1);
    }

    // --- GetById ---

    [Fact]
    public async Task GetById_ExistingApplication_Returns200()
    {
        Reset();
        var app = SeedApplication();
        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetById_MissingApplication_Returns404()
    {
        Reset();
        var response = await _client.GetAsync(
            "/api/v1/accreditation-applications/org-123/000000000000000000000000",
            cancellationToken: TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- PatchPrns ---

    [Fact]
    public async Task PatchPrns_ValidRequest_TransitionsStatusToStarted()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

        var request = new PatchPrnsRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    // --- PatchBusinessPlan ---

    [Fact]
    public async Task PatchBusinessPlan_PercentsNotSumTo100_Returns422()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchBusinessPlanRequest
        {
            NewInfrastructurePercent = 10,
            PriceSupportPercent = 10,
            BusinessCollectionsPercent = 10,
            CommunicationsPercent = 10,
            NewMarketsPercent = 10,
            NewUsesPercent = 10 // sum = 60
        };

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // --- Submit ---

    [Fact]
    public async Task Submit_AllSectionsCompleted_Returns200WithReference()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started, configure: a =>
        {
            a.Prns.SectionStatus = SectionStatus.Completed;
            a.BusinessPlan.SectionStatus = SectionStatus.Completed;
            a.SamplingPlan.SectionStatus = SectionStatus.Completed;
        });
        _factory.MockCaseWorkingAdapter
            .SubmitApplicationAsync(Arg.Any<AccreditationApplicationModel>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new SubmitRequest { FullName = "John Operator", JobTitle = "Operations Manager", Email = "john@example.com" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Sent);
        body.ApplicationReference.Should().MatchRegex(@"^EPR-ACC-2026-[A-Z0-9]{7}$");
        body.DateSent.Should().NotBeNull();
        await _factory.MockCaseWorkingAdapter
            .Received(1)
            .SubmitApplicationAsync(Arg.Any<AccreditationApplicationModel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_SectionsIncomplete_Returns400()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new SubmitRequest { FullName = "John", JobTitle = "Manager", Email = "j@x.com" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_WhenAlreadySent_ReturnsIdempotentOk()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Sent, configure: a =>
            a.ApplicationReference = "EPR-ACC-2026-ABC1234");

        var request = new SubmitRequest { FullName = "John", JobTitle = "Manager", Email = "j@x.com" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.MockCaseWorkingAdapter
            .DidNotReceive()
            .SubmitApplicationAsync(Arg.Any<AccreditationApplicationModel>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_WhenSaved_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

        var request = new SubmitRequest { FullName = "John", JobTitle = "Manager", Email = "j@x.com" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Submit_WhenApproved_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Approved);

        var request = new SubmitRequest { FullName = "John", JobTitle = "Manager", Email = "j@x.com" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Submit_WhenAdapterThrows_ApplicationRemainsStarted()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started, configure: a =>
        {
            a.Prns.SectionStatus = SectionStatus.Completed;
            a.BusinessPlan.SectionStatus = SectionStatus.Completed;
            a.SamplingPlan.SectionStatus = SectionStatus.Completed;
        });
        _factory.MockCaseWorkingAdapter
            .SubmitApplicationAsync(Arg.Any<AccreditationApplicationModel>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("adapter unavailable")));

        var request = new SubmitRequest { FullName = "John", JobTitle = "Manager", Email = "j@x.com" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var stored = await _factory.FakePersistence.GetByIdAsync("org-123", app.Id!.Value.ToString());
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
        stored.ApplicationReference.Should().BeNull();
    }

    // --- Approve ---

    [Fact]
    public async Task Approve_SetsApprovedStatusAndCallsReExAdapter()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Sent, configure: a =>
            a.ApplicationReference = "EPR-ACC-2026-ABC1234");
        _factory.MockReExAdapter
            .WriteApprovedAccreditationAsync(Arg.Any<ApprovedAccreditationDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve", null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        await _factory.MockReExAdapter
            .Received(1)
            .WriteApprovedAccreditationAsync(Arg.Any<ApprovedAccreditationDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_WhenAlreadyApproved_ReturnsIdempotentOk()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Approved);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve", null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory.MockReExAdapter
            .DidNotReceive()
            .WriteApprovedAccreditationAsync(Arg.Any<ApprovedAccreditationDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_WhenNotSent_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve", null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approve_WhenAdapterThrows_ApplicationRemainsSent()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Sent, configure: a =>
            a.ApplicationReference = "EPR-ACC-2026-ABC1234");
        _factory.MockReExAdapter
            .WriteApprovedAccreditationAsync(Arg.Any<ApprovedAccreditationDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("adapter unavailable")));

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve", null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var stored = await _factory.FakePersistence.GetByIdAsync("org-123", app.Id!.Value.ToString());
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Sent);
    }

    // --- Reject ---

    [Fact]
    public async Task Reject_SetsRejectedStatus()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Sent);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/reject", null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Rejected);
    }

    [Fact]
    public async Task Reject_WhenAlreadyRejected_ReturnsIdempotentOk()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Rejected);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/reject", null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reject_WhenNotSent_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/reject", null,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- AddFile ---

    [Fact]
    public async Task AddFile_AddsFileToSamplingPlan_Returns201()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest { FileId = "file-001", Filename = "plan.pdf", ContentType = "application/pdf" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddFile_InvalidFilename_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest { FileId = "file-002", Filename = "../../etc/passwd", ContentType = "application/pdf" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddFile_ForbiddenContentType_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest { FileId = "file-003", Filename = "script.js", ContentType = "text/javascript" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddFile_ExceedsMaxFileCount_Returns422()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            for (var i = 0; i < 10; i++)
                a.SamplingPlan.Files.Add(new AccreditationApplicationFile
                {
                    FileId = $"existing-{i}", Filename = $"file{i}.pdf",
                    ContentType = "application/pdf", UploadedByUserId = string.Empty
                });
        });

        var request = new FileUploadRequest { FileId = "file-new", Filename = "new.pdf", ContentType = "application/pdf" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files", request,
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // --- DeleteFile ---

    [Fact]
    public async Task DeleteFile_ExistingFile_Returns200()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.SamplingPlan.Files.Add(new AccreditationApplicationFile
            {
                FileId = "file-001", Filename = "plan.pdf", ContentType = "application/pdf",
                UploadedByUserId = string.Empty
            }));

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/file-001",
            cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
