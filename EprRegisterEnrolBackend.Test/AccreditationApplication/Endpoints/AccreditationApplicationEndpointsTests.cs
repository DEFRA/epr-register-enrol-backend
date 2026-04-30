using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;
using Xunit;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationEndpointsTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void ResetMocks()
    {
        _factory.MockPersistence.ClearSubstitute(ClearOptions.All);
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
    }

    private static AccreditationApplicationModel BuildApplication(
        string orgId = "org-123",
        ApplicationStatus status = ApplicationStatus.Saved) => new()
    {
        Id = ObjectId.GenerateNewId(),
        OrganisationId = orgId,
        Year = 2026,
        MaterialType = MaterialType.Steel,
        ApplicationStatus = status
    };

    [Fact]
    public async Task Seed_ValidRequest_Returns201WithApplication()
    {
        ResetMocks();
        _factory.MockReExAdapter
            .GetAccreditationAsync(Arg.Any<string>(), Arg.Any<MaterialType>(), Arg.Any<int>())
            .Returns(Task.FromResult<ReExAccreditationDto?>(null));
        _factory.MockPersistence
            .CreateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));

        var request = new SeedRequest { MaterialType = MaterialType.Steel, Year = 2026 };
        var response = await _client.PostAsJsonAsync("/api/v1/accreditation-applications/org-123/seed", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>();
        body!.OrganisationId.Should().Be("org-123");
        body.MaterialType.Should().Be(MaterialType.Steel);
        body.ApplicationStatus.Should().Be(ApplicationStatus.Saved);
    }

    [Fact]
    public async Task Seed_WithPriorYearData_PrePopulatesFields()
    {
        ResetMocks();
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
        _factory.MockPersistence
            .CreateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));

        var request = new SeedRequest { MaterialType = MaterialType.Steel, Year = 2026 };
        var response = await _client.PostAsJsonAsync("/api/v1/accreditation-applications/org-123/seed", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>();
        body!.SourceReExAccreditationId.Should().Be("reex-abc");
        body.SourceYear.Should().Be(2025);
        body.Prns.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo1000);
    }

    [Fact]
    public async Task Seed_InvalidYear_Returns400()
    {
        ResetMocks();
        var request = new SeedRequest { MaterialType = MaterialType.Steel, Year = 2020 };
        var response = await _client.PostAsJsonAsync("/api/v1/accreditation-applications/org-123/seed", request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetList_ReturnsApplicationsForOrg()
    {
        ResetMocks();
        var app = BuildApplication();
        _factory.MockPersistence
            .GetByOrganisationAsync("org-123")
            .Returns(Task.FromResult<IEnumerable<AccreditationApplicationModel>>([app]));

        var response = await _client.GetAsync("/api/v1/accreditation-applications/org-123");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AccreditationApplicationModel>>();
        body.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetById_ExistingApplication_Returns200()
    {
        ResetMocks();
        var app = BuildApplication();
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));

        var response = await _client.GetAsync($"/api/v1/accreditation-applications/org-123/{appId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetById_MissingApplication_Returns404()
    {
        ResetMocks();
        _factory.MockPersistence
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult<AccreditationApplicationModel?>(null));

        var response = await _client.GetAsync("/api/v1/accreditation-applications/org-123/000000000000000000000000");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchPrns_ValidRequest_TransitionsStatusToStarted()
    {
        ResetMocks();
        var app = BuildApplication(status: ApplicationStatus.Saved);
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));
        _factory.MockPersistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));

        var request = new PatchPrnsRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{appId}/prns", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>();
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task PatchBusinessPlan_PercentsNotSumTo100_Returns422()
    {
        ResetMocks();
        var app = BuildApplication();
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));

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
            $"/api/v1/accreditation-applications/org-123/{appId}/business-plan", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Submit_AllSectionsCompleted_Returns200WithReference()
    {
        ResetMocks();
        var app = BuildApplication(status: ApplicationStatus.Started);
        app.Prns.SectionStatus = SectionStatus.Completed;
        app.BusinessPlan.SectionStatus = SectionStatus.Completed;
        app.SamplingPlan.SectionStatus = SectionStatus.Completed;
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));
        _factory.MockPersistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));
        _factory.MockCaseWorkingAdapter
            .SubmitApplicationAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(Task.CompletedTask);

        var request = new SubmitRequest
        {
            FullName = "John Operator",
            JobTitle = "Operations Manager",
            Email = "john@example.com"
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{appId}/submit", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>();
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Sent);
        body.ApplicationReference.Should().MatchRegex(@"^EPR-ACC-2026-[A-Z0-9]{7}$");
        body.DateSent.Should().NotBeNull();
        await _factory.MockCaseWorkingAdapter
            .Received(1)
            .SubmitApplicationAsync(Arg.Any<AccreditationApplicationModel>());
    }

    [Fact]
    public async Task Submit_SectionsIncomplete_Returns400()
    {
        ResetMocks();
        var app = BuildApplication(status: ApplicationStatus.Started);
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));

        var request = new SubmitRequest { FullName = "John", JobTitle = "Manager", Email = "j@x.com" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{appId}/submit", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Approve_SetsApprovedStatusAndCallsReExAdapter()
    {
        ResetMocks();
        var app = BuildApplication(status: ApplicationStatus.Sent);
        app.ApplicationReference = "EPR-ACC-2026-ABC1234";
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));
        _factory.MockPersistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));
        _factory.MockReExAdapter
            .WriteApprovedAccreditationAsync(Arg.Any<ApprovedAccreditationDto>())
            .Returns(Task.CompletedTask);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{appId}/approve", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>();
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        await _factory.MockReExAdapter
            .Received(1)
            .WriteApprovedAccreditationAsync(Arg.Any<ApprovedAccreditationDto>());
    }

    [Fact]
    public async Task Reject_SetsRejectedStatus()
    {
        ResetMocks();
        var app = BuildApplication(status: ApplicationStatus.Sent);
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));
        _factory.MockPersistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{appId}/reject", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>();
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Rejected);
    }

    [Fact]
    public async Task AddFile_AddsFileToSamplingPlan_Returns201()
    {
        ResetMocks();
        var app = BuildApplication();
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));
        _factory.MockPersistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));

        var request = new FileUploadRequest
        {
            FileId = "file-001",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            UploadedByUserId = "user-1"
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{appId}/files", request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task DeleteFile_ExistingFile_Returns200()
    {
        ResetMocks();
        var app = BuildApplication();
        app.SamplingPlan.Files.Add(new AccreditationApplicationFile
        {
            FileId = "file-001", Filename = "plan.pdf", ContentType = "application/pdf",
            UploadedByUserId = "user-1"
        });
        var appId = app.Id!.Value.ToString();
        _factory.MockPersistence
            .GetByIdAsync("org-123", appId)
            .Returns(Task.FromResult<AccreditationApplicationModel?>(app));
        _factory.MockPersistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(c => Task.FromResult<AccreditationApplicationModel?>(c.Arg<AccreditationApplicationModel>()));

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{appId}/files/file-001");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
