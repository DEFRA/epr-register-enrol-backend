using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationEndpointsTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
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
        _factory.MockOrganisationPersistence.ClearSubstitute(ClearOptions.All);
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

    private static ReExResult<ReExAccreditationDto> MinimalAdapterSuccess(
        string orgId = "org-123",
        MaterialType material = MaterialType.Steel,
        int year = 2025
    ) =>
        ReExResult<ReExAccreditationDto>.Success(
            new ReExAccreditationDto
            {
                AccreditationId = $"reex-acc-{orgId}-{material}-{year}",
                OrganisationId = orgId,
                MaterialType = material,
                Year = year,
                OrganisationName = "Stub Org Ltd",
                IsExporter = false,
                OverseasSites = [],
            },
            200
        );

    // --- Seed ---

    [Fact]
    public async Task Seed_ValidRequest_Returns201WithApplication()
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(Task.FromResult(MinimalAdapterSuccess()));

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OrganisationId.Should().Be("org-123");
        body.MaterialType.Should().Be(MaterialType.Steel);
        body.ApplicationStatus.Should().Be(ApplicationStatus.Saved);
    }

    [Fact]
    public async Task Seed_WithPriorYearData_PrePopulatesFields()
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync("org-123", "reg-1", MaterialType.Steel, 2025)
            .Returns(
                Task.FromResult(
                    ReExResult<ReExAccreditationDto>.Success(
                        new ReExAccreditationDto
                        {
                            AccreditationId = "reex-abc",
                            OrganisationId = "org-123",
                            MaterialType = MaterialType.Steel,
                            Year = 2025,
                            IsExporter = false,
                            OverseasSites = [],
                            Prns = new ReExPrnsDto
                            {
                                PlannedTonnageBand = PlannedTonnageBand.UpTo1000,
                            },
                            BusinessPlan = new ReExBusinessPlanDto
                            {
                                NewInfrastructurePercent = 20,
                            },
                        },
                        200
                    )
                )
            );

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.SourceReExAccreditationId.Should().Be("reex-abc");
        body.SourceYear.Should().Be(2025);
        body.Prns.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo1000);
    }

    [Fact]
    public async Task Seed_InvalidYear_Returns400()
    {
        Reset();
        var request = new SeedRequest { Year = 2020 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Seed_InvalidMaterialType_Returns400()
    {
        Reset();
        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Unknown/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Seed_ValidRequest_MaterialTypeAndStatusSerializedAsStrings()
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(Task.FromResult(MinimalAdapterSuccess()));

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        );
        json.RootElement.GetProperty("materialType").ValueKind.Should().Be(JsonValueKind.String);
        json.RootElement.GetProperty("applicationStatus")
            .ValueKind.Should()
            .Be(JsonValueKind.String);
        json.RootElement.GetProperty("materialType").GetString().Should().Be("Steel");
        json.RootElement.GetProperty("applicationStatus").GetString().Should().Be("Saved");
    }

    [Fact]
    public async Task Seed_PopulatesOrganisationDataFromAdapter()
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(
                Task.FromResult(
                    ReExResult<ReExAccreditationDto>.Success(
                        new ReExAccreditationDto
                        {
                            AccreditationId = "reex-acc-1",
                            OrganisationId = "org-123",
                            MaterialType = MaterialType.Steel,
                            Year = 2025,
                            OrganisationName = "Acme Reprocessing Ltd",
                            RegistrationReference = "REP-001",
                            SiteAddress = "1 Factory Lane, Manchester, M1 1AA",
                            IsExporter = false,
                            OverseasSites = [],
                        },
                        200
                    )
                )
            );

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OrganisationName.Should().Be("Acme Reprocessing Ltd");
        body.SiteAddress.Should().Be("1 Factory Lane, Manchester, M1 1AA");
        body.RegistrationReference.Should().Be("REP-001");
    }

    [Fact]
    public async Task Seed_WhenAdapterReturnsNotFound_Returns404()
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(
                Task.FromResult(
                    ReExResult<ReExAccreditationDto>.Fail(
                        new ReExError(ReExErrorKind.NotFound, "No prior year accreditation found"),
                        404
                    )
                )
            );

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Seed_WhenAdapterReturnsUpstreamFailure_Returns502()
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(
                Task.FromResult(
                    ReExResult<ReExAccreditationDto>.Fail(
                        new ReExError(ReExErrorKind.ServerError, "Upstream error"),
                        500
                    )
                )
            );

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Seed_DuplicateSeed_ReturnsExistingDocument_WithoutCallingAdapter()
    {
        Reset();
        SeedApplication(
            orgId: "org-123",
            configure: a =>
            {
                a.RegistrationId = "reg-1";
                a.MaterialType = MaterialType.Steel;
                a.Year = 2026;
            }
        );

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockReExAdapter.DidNotReceive()
            .GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            );
    }

    // --- GetList ---

    [Fact]
    public async Task GetList_ReturnsApplicationsForOrg()
    {
        Reset();
        SeedApplication();

        var response = await _client.GetAsync(
            "/api/v1/accreditation-applications/org-123",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<AccreditationApplicationModel>>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
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
            cancellationToken: TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetById_MissingApplication_Returns404()
    {
        Reset();
        var response = await _client.GetAsync(
            "/api/v1/accreditation-applications/org-123/000000000000000000000000",
            cancellationToken: TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetById_LinkedWorkItem_ReturnsAdapterNotificationStatus()
    {
        Reset();
        var app = SeedApplication(configure: a => a.CaseManagementWorkItemId = Guid.NewGuid());
        _factory
            .MockCaseWorkingAdapter.GetNotificationStatusAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult<string?>("failed"));

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.NotificationStatus.Should().Be("failed");
    }

    [Fact]
    public async Task GetById_NoLinkedWorkItem_NotificationStatusIsNull()
    {
        Reset();
        var app = SeedApplication(configure: a => a.CaseManagementWorkItemId = null);

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.NotificationStatus.Should().BeNull();
        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .GetNotificationStatusAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task GetById_AdapterThrows_Returns200WithNullNotificationStatus()
    {
        Reset();
        var app = SeedApplication(configure: a => a.CaseManagementWorkItemId = Guid.NewGuid());
        _factory
            .MockCaseWorkingAdapter.GetNotificationStatusAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException<string?>(new HttpRequestException("unreachable")));

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.NotificationStatus.Should().BeNull();
    }

    // --- PatchPrns ---

    [Fact]
    public async Task PatchPrns_ValidRequest_TransitionsStatusToStarted()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

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

    // --- PatchTonnage ---

    [Fact]
    public async Task PatchTonnage_WithAuthorisersOnly_UpdatesAuthorisersLeavesTonnageBandUnchanged()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Saved,
            configure: a => a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo1000
        );

        var request = new PatchTonnageRequest
        {
            Authorisers =
            [
                new PrnsAuthoriser { FullName = "Jane Smith", Email = "jane@example.com" },
            ],
        };
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
        body!.Prns.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo1000);
        body.Prns.Authorisers.Should().ContainSingle(a => a.FullName == "Jane Smith");
    }

    [Fact]
    public async Task PatchTonnage_WithPlannedTonnageBandOnly_UpdatesTonnageBandLeavesAuthorisersUnchanged()
    {
        Reset();
        var existingAuthoriser = new PrnsAuthoriser
        {
            FullName = "Jane Smith",
            Email = "jane@example.com",
        };
        var app = SeedApplication(
            status: ApplicationStatus.Saved,
            configure: a => a.Prns.Authorisers = [existingAuthoriser]
        );

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
        body!.Prns.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo500);
        body.Prns.Authorisers.Should().ContainSingle(a => a.FullName == "Jane Smith");
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
            NewUsesPercent = 10, // sum = 60
        };

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // --- Submit ---

    [Fact]
    public async Task Submit_AllSectionsCompleted_Returns200WithReference()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult(new CaseWorkingSubmissionResult("RA-123456789", Guid.NewGuid()))
            );

        var request = new SubmitRequest
        {
            FullName = "John Operator",
            JobTitle = "Operations Manager",
            Email = "john@example.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SubmitResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.AccreditationReference.Should().Be("RA-123456789");
        body.CaseManagementReference.Should().Be("RA-123456789");
        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Submit_Success_PersistsCaseManagementWorkItemId()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        var workItemId = Guid.NewGuid();
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new CaseWorkingSubmissionResult("RA-123456789", workItemId)));

        var request = new SubmitRequest
        {
            FullName = "John Operator",
            JobTitle = "Operations Manager",
            Email = "john@example.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.CaseManagementWorkItemId.Should().Be(workItemId);
    }

    [Fact]
    public async Task Submit_AdapterReturnsNullWorkItemId_PersistsNullWithoutFailingSubmission()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new CaseWorkingSubmissionResult("RA-123456789", null)));

        var request = new SubmitRequest
        {
            FullName = "John Operator",
            JobTitle = "Operations Manager",
            Email = "john@example.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationReference.Should().Be("RA-123456789");
        stored.CaseManagementWorkItemId.Should().BeNull();
    }

    [Fact]
    public async Task Submit_SectionsIncomplete_Returns400()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_WhenAlreadySent_ReturnsIdempotentOk()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.ApplicationReference = "RA-123456789"
        );

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SubmitResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.AccreditationReference.Should().Be("RA-123456789");
        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Submit_WhenSaved_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Submit_WhenApproved_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Approved);

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Submit_WhenAdapterThrows_ApplicationRemainsStarted()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
            }
        );
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromException<CaseWorkingSubmissionResult>(
                    new HttpRequestException("adapter unavailable")
                )
            );

        var request = new SubmitRequest
        {
            FullName = "John",
            JobTitle = "Manager",
            Email = "j@x.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/submit",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
        stored.ApplicationReference.Should().BeNull();
    }

    // --- Approve ---

    [Fact]
    public async Task Approve_SetsApprovedStatusAndCallsReExAdapter()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.ApplicationReference = "RA-123456789"
        );
        _factory
            .MockReExAdapter.WriteApprovedAccreditationAsync(
                Arg.Any<ApprovedAccreditationDto>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(ReExResult<bool>.Success(true, 200)));

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve",
            null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        await _factory
            .MockReExAdapter.Received(1)
            .WriteApprovedAccreditationAsync(
                Arg.Any<ApprovedAccreditationDto>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Approve_WhenAlreadyApproved_ReturnsIdempotentOk()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Approved);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve",
            null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockReExAdapter.DidNotReceive()
            .WriteApprovedAccreditationAsync(
                Arg.Any<ApprovedAccreditationDto>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Approve_WhenNotSent_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve",
            null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Approve_WhenAdapterFails_Returns502AndApplicationRemainsSent()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.ApplicationReference = "RA-123456789"
        );
        _factory
            .MockReExAdapter.WriteApprovedAccreditationAsync(
                Arg.Any<ApprovedAccreditationDto>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromResult(
                    ReExResult<bool>.Fail(
                        new ReExError(ReExErrorKind.ServerError, "upstream failure")
                    )
                )
            );

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/approve",
            null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Submitted);
    }

    // --- Reject ---

    [Fact]
    public async Task Reject_SetsRejectedStatus()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Submitted);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/reject",
            null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Rejected);
    }

    [Fact]
    public async Task Reject_WhenAlreadyRejected_ReturnsIdempotentOk()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Rejected);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/reject",
            null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Reject_WhenNotSent_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Saved);

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/reject",
            null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- AddFile ---

    [Fact]
    public async Task AddFile_AddsFileToSamplingPlan_Returns201()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest
        {
            FileId = "file-001",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            S3Key = "sampling-plans/file-001",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddFile_InvalidFilename_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest
        {
            FileId = "file-002",
            Filename = "../../etc/passwd",
            ContentType = "application/pdf",
            S3Key = "sampling-plans/file-002",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddFile_ForbiddenContentType_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest
        {
            FileId = "file-003",
            Filename = "script.js",
            ContentType = "text/javascript",
            S3Key = "sampling-plans/file-003",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddFile_ExceedsMaxFileCount_Returns422()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            for (var i = 0; i < 10; i++)
                a.SamplingPlan.Files.Add(
                    new AccreditationApplicationFile
                    {
                        FileId = $"existing-{i}",
                        Filename = $"file{i}.pdf",
                        ContentType = "application/pdf",
                        UploadedByUserId = string.Empty,
                        S3Key = $"sampling-plans/existing-{i}",
                    }
                );
        });

        var request = new FileUploadRequest
        {
            FileId = "file-new",
            Filename = "new.pdf",
            ContentType = "application/pdf",
            S3Key = "sampling-plans/file-new",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // --- DeleteFile ---

    [Fact]
    public async Task DeleteFile_ExistingFile_Returns200()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.SamplingPlan.Files.Add(
                new AccreditationApplicationFile
                {
                    FileId = "file-001",
                    Filename = "plan.pdf",
                    ContentType = "application/pdf",
                    UploadedByUserId = string.Empty,
                    S3Key = "sampling-plans/file-001",
                }
            )
        );

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- CDP Upload ---

    [Fact]
    public async Task InitiateUpload_Returns200WithUploadDetails()
    {
        Reset();
        var app = SeedApplication();

        _factory
            .MockCdpUploaderService.InitiateAsync(
                Arg.Any<CdpInitiateRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CdpInitiateResponse
                {
                    UploadId = "cdp-upload-id",
                    UploadUrl = "http://localhost:7337/upload/cdp-upload-id",
                    StatusUrl = "http://localhost:7337/status/cdp-upload-id",
                }
            );

        var request = new
        {
            redirectUrl = "http://frontend/redirect",
            s3Bucket = "test-bucket",
            s3Path = "uploads/test.csv",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/initiate",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<InitiateUploadResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.UploadUrl.Should().Be("http://localhost:7337/upload/cdp-upload-id");
        body.StatusUrl.Should().Contain("/files/").And.Contain("/status");
        body.FileUploadId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InitiateUpload_SendsClientSuppliedBucketAndPrefixedPath()
    {
        Reset();
        var app = SeedApplication();

        _factory
            .MockCdpUploaderService.InitiateAsync(
                Arg.Any<CdpInitiateRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CdpInitiateResponse
                {
                    UploadId = "cdp-upload-id",
                    UploadUrl = "http://localhost:7337/upload/cdp-upload-id",
                    StatusUrl = "http://localhost:7337/status/cdp-upload-id",
                }
            );

        var request = new
        {
            redirectUrl = "http://frontend/redirect",
            s3Bucket = "test-epr-register-enrol-bucket",
            s3Path = "uploads/test.csv",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/initiate",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCdpUploaderService.Received(1)
            .InitiateAsync(
                Arg.Is<CdpInitiateRequest>(r =>
                    r.S3Bucket == "test-epr-register-enrol-bucket"
                    && r.S3Path == "sampling-plans/uploads/test.csv"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    // --- AddBesEvidenceFile ---

    private AccreditationApplicationModel SeedApplicationWithOverseasSite(
        int siteId = 1,
        string siteName = "Test Site"
    ) =>
        SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [new OverseasSiteModel { SiteId = siteId, SiteName = siteName }],
            }
        );

    [Fact]
    public async Task AddBesEvidenceFile_AddsFileToSite_Returns201()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = new AddBesEvidenceFileRequest
        {
            FileId = "bes-file-001",
            Filename = "evidence.pdf",
            S3Key = "bes-evidence/bes-file-001",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddBesEvidenceFile_EmptyS3Key_Returns400()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = new AddBesEvidenceFileRequest
        {
            FileId = "bes-file-002",
            Filename = "evidence.pdf",
            S3Key = string.Empty,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddBesEvidenceFile_InvalidFilename_Returns400()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = new AddBesEvidenceFileRequest
        {
            FileId = "bes-file-003",
            Filename = "../../etc/passwd",
            S3Key = "bes-evidence/bes-file-003",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task InitiateBesEvidenceUpload_PrefixesPathWithBesEvidenceBucket()
    {
        Reset();
        var app = SeedApplication();

        _factory
            .MockCdpUploaderService.InitiateAsync(
                Arg.Any<CdpInitiateRequest>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new CdpInitiateResponse
                {
                    UploadId = "cdp-upload-id",
                    UploadUrl = "http://localhost:7337/upload/cdp-upload-id",
                    StatusUrl = "http://localhost:7337/status/cdp-upload-id",
                }
            );

        var request = new
        {
            redirectUrl = "http://frontend/redirect",
            s3Bucket = "test-epr-register-enrol-bucket",
            s3Path = "accreditation/bes-evidence/test.pdf",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/bes-evidence/initiate",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCdpUploaderService.Received(1)
            .InitiateAsync(
                Arg.Is<CdpInitiateRequest>(r =>
                    r.S3Bucket == "test-epr-register-enrol-bucket"
                    && r.S3Path == "bes-evidence/accreditation/bes-evidence/test.pdf"
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task UploadCompleted_ValidPayload_Returns200()
    {
        Reset();

        var payload = new
        {
            form = new
            {
                file = new
                {
                    fileId = "file-xyz",
                    filename = "plan.csv",
                    fileStatus = "complete",
                },
            },
            metadata = new Dictionary<string, string> { ["fileUploadId"] = "upload-abc" },
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/files/upload-completed",
            payload,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UploadCompleted_MissingFileUploadId_Returns400()
    {
        Reset();

        var payload = new
        {
            form = new { file = new { fileId = "file-xyz" } },
            metadata = new Dictionary<string, string>(),
        };

        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/files/upload-completed",
            payload,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetUploadStatus_AfterComplete_ReturnsReady()
    {
        Reset();
        var app = SeedApplication();

        var fileUploadId = "test-upload-id";
        var callbackPayload = new
        {
            form = new
            {
                file = new
                {
                    fileId = "file-123",
                    filename = "test.csv",
                    fileStatus = "complete",
                },
            },
            metadata = new Dictionary<string, string> { ["fileUploadId"] = fileUploadId },
        };
        await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/files/upload-completed",
            callbackPayload,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/{fileUploadId}/status",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CdpStatusResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.UploadStatus.Should().Be("ready");
        body.Form!.File!.FileId.Should().Be("file-123");
    }

    [Fact]
    public async Task GetUploadStatus_BeforeComplete_ReturnsPending()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/unknown-upload-id/status",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CdpStatusResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.UploadStatus.Should().Be("pending");
    }
}
