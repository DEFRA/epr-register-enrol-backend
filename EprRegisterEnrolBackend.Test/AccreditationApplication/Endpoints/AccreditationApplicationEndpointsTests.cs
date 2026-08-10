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

    [Theory]
    [InlineData("undefined")]
    [InlineData("Undefined")]
    [InlineData("null")]
    [InlineData("Null")]
    [InlineData("%20")] // whitespace-only registrationId — exercises the IsNullOrWhiteSpace branch
    public async Task Seed_InvalidRegistrationId_Returns400(string registrationId)
    {
        Reset();
        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{registrationId}/Steel/seed",
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
                            CompanyRegisterAddressPostcode = "EC1A 1BB",
                            WasteProcessingType = "reprocessor",
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
        body.CompanyRegisterAddressPostcode.Should().Be("EC1A 1BB");
        body.WasteProcessingType.Should().Be("reprocessor");
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

    // --- Seed: restart after withdrawal (RA-357) ---

    private void ArrangeAdapterSuccess() =>
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(Task.FromResult(MinimalAdapterSuccess()));

    private async Task<List<AccreditationApplicationModel>> StoredApplications(
        string orgId = "org-123"
    ) => (await _factory.FakePersistence.GetByOrganisationAsync(orgId)).ToList();

    private Task<HttpResponseMessage> PostSeed(int year = 2026) =>
        _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            new SeedRequest { Year = year },
            cancellationToken: TestContext.Current.CancellationToken
        );

    // `applicationId` is a computed, getter-only projection of the BSON `_id` (and `Id` itself is
    // never serialised), so it cannot round-trip back into the model — read it from the raw JSON.
    private static async Task<(
        AccreditationApplicationModel Model,
        string? ApplicationId
    )> ReadApplication(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var model = JsonSerializer.Deserialize<AccreditationApplicationModel>(raw, JsonOptions)!;
        var applicationId = JsonDocument
            .Parse(raw)
            .RootElement.GetProperty("applicationId")
            .GetString();
        return (model, applicationId);
    }

    private AccreditationApplicationModel SeedWithdrawn(string reason = "no longer required") =>
        SeedApplication(
            status: ApplicationStatus.Withdrawn,
            configure: a =>
            {
                a.RegistrationId = "reg-1";
                a.MaterialType = MaterialType.Steel;
                a.Year = 2026;
                a.WithdrawalReason = reason;
            }
        );

    [Fact]
    public async Task Seed_WhenOnlyMatchIsWithdrawn_CreatesNewApplicationForSameYear()
    {
        Reset();
        ArrangeAdapterSuccess();
        var withdrawn = SeedWithdrawn();

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var (body, applicationId) = await ReadApplication(response);
        body.ApplicationStatus.Should().Be(ApplicationStatus.Saved);
        body.Year.Should().Be(2026, "a restart stays on the same accreditation year");
        body.RegistrationId.Should().Be("reg-1");
        body.MaterialType.Should().Be(MaterialType.Steel);
        applicationId.Should().NotBeNullOrEmpty().And.NotBe(withdrawn.ApplicationId);

        // AC2: the prior-year ReEx lookup is unchanged — still year - 1.
        await _factory
            .MockReExAdapter.Received(1)
            .GetAccreditationAsync("org-123", "reg-1", MaterialType.Steel, 2025);
        body.SourceYear.Should().Be(2025);
    }

    [Fact]
    public async Task Seed_WhenOnlyMatchIsWithdrawn_LeavesWithdrawnApplicationUntouched()
    {
        Reset();
        ArrangeAdapterSuccess();
        var withdrawn = SeedWithdrawn();
        var originalId = withdrawn.ApplicationId;
        var originalEdited = withdrawn.DateLastEdited;
        var originalCreated = withdrawn.CreatedAt;

        (await PostSeed()).StatusCode.Should().Be(HttpStatusCode.Created);

        var stored = await StoredApplications();
        stored.Should().HaveCount(2, "the withdrawn record is retained alongside the new one");

        var retained = stored.Single(a => a.ApplicationId == originalId);
        retained.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        retained.WithdrawalReason.Should().Be("no longer required");
        retained.Year.Should().Be(2026);
        retained.DateLastEdited.Should().Be(originalEdited);
        retained.CreatedAt.Should().Be(originalCreated);

        stored
            .Should()
            .ContainSingle(a => a.ApplicationStatus == ApplicationStatus.Saved)
            .Which.Year.Should()
            .Be(2026);
    }

    [Fact]
    public async Task Seed_WhenLiveApplicationExists_ReturnsExistingAndCreatesNothing()
    {
        Reset();
        var live = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.RegistrationId = "reg-1";
                a.MaterialType = MaterialType.Steel;
                a.Year = 2026;
            }
        );

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (_, applicationId) = await ReadApplication(response);
        applicationId.Should().Be(live.ApplicationId);
        (await StoredApplications()).Should().HaveCount(1);
        await _factory
            .MockReExAdapter.DidNotReceive()
            .GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            );
    }

    [Fact]
    public async Task Seed_WhenWithdrawnAndLiveExistForSameYear_ReturnsTheLiveOne()
    {
        Reset();
        SeedWithdrawn();
        var live = SeedApplication(
            status: ApplicationStatus.Saved,
            configure: a =>
            {
                a.RegistrationId = "reg-1";
                a.MaterialType = MaterialType.Steel;
                a.Year = 2026;
            }
        );

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (body, applicationId) = await ReadApplication(response);
        applicationId.Should().Be(live.ApplicationId);
        body.ApplicationStatus.Should().Be(ApplicationStatus.Saved);
        (await StoredApplications()).Should().HaveCount(2, "nothing new is created");
    }

    [Fact]
    public async Task Seed_WhenMultipleWithdrawnAndNoLive_CreatesExactlyOneNewApplication()
    {
        Reset();
        ArrangeAdapterSuccess();
        var first = SeedWithdrawn("first attempt");
        var second = SeedWithdrawn("second attempt");

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await StoredApplications();
        stored.Should().HaveCount(3);
        stored
            .Should()
            .ContainSingle(a => a.ApplicationStatus == ApplicationStatus.Saved)
            .Which.Year.Should()
            .Be(2026);
        stored
            .Where(a => a.ApplicationStatus == ApplicationStatus.Withdrawn)
            .Select(a => a.ApplicationId)
            .Should()
            .BeEquivalentTo([first.ApplicationId, second.ApplicationId]);
    }

    [Theory]
    // Each row makes exactly one clause of the live-application predicate false, so the seed
    // must fall through and create a new application rather than returning the seeded record.
    [InlineData("reg-other", MaterialType.Steel, 2026)] // registrationId differs
    [InlineData("reg-1", MaterialType.Wood, 2026)] // materialType differs
    [InlineData("reg-1", MaterialType.Steel, 2027)] // year differs
    public async Task Seed_WhenExistingApplicationDoesNotMatchKey_CreatesNewApplication(
        string registrationId,
        MaterialType materialType,
        int year
    )
    {
        Reset();
        ArrangeAdapterSuccess();
        SeedApplication(configure: a =>
        {
            a.RegistrationId = registrationId;
            a.MaterialType = materialType;
            a.Year = year;
        });

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await StoredApplications()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Seed_WithMultipleLiveApplications_ReturnsTheMostRecentlyCreated()
    {
        Reset();
        var older = SeedApplication(configure: a =>
        {
            a.RegistrationId = "reg-1";
            a.MaterialType = MaterialType.Steel;
            a.Year = 2026;
            a.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        });
        var newer = SeedApplication(configure: a =>
        {
            a.RegistrationId = "reg-1";
            a.MaterialType = MaterialType.Steel;
            a.Year = 2026;
            a.CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        });

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (_, applicationId) = await ReadApplication(response);
        applicationId.Should().Be(newer.ApplicationId).And.NotBe(older.ApplicationId);
    }

    [Fact]
    public async Task Seed_WithLiveApplicationsSharingCreatedAt_BreaksTheTieDeterministicallyById()
    {
        Reset();
        var createdAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var idA = ObjectId.GenerateNewId();
        var idB = ObjectId.GenerateNewId();
        var expectedId = idA > idB ? idA : idB;

        foreach (var id in new[] { idA, idB })
        {
            SeedApplication(configure: a =>
            {
                a.Id = id;
                a.RegistrationId = "reg-1";
                a.MaterialType = MaterialType.Steel;
                a.Year = 2026;
                a.CreatedAt = createdAt;
            });
        }

        var response = await PostSeed();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (_, applicationId) = await ReadApplication(response);
        applicationId.Should().Be(expectedId.ToString());
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

    // RA-357: the list is now one-to-many per (registrationId, materialType, year), so its order
    // is a contract. Ids come from the raw JSON — `applicationId` cannot round-trip into the model.
    private static async Task<List<string?>> ReadApplicationIds(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonDocument
            .Parse(raw)
            .RootElement.EnumerateArray()
            .Select(e => e.GetProperty("applicationId").GetString())
            .ToList();
    }

    private Task<HttpResponseMessage> GetList() =>
        _client.GetAsync(
            "/api/v1/accreditation-applications/org-123",
            cancellationToken: TestContext.Current.CancellationToken
        );

    [Fact]
    public async Task GetList_OrdersByCreatedAtDescending()
    {
        Reset();
        // Seeded oldest-first so a pass cannot come from incidental insertion order.
        var oldest = SeedApplication(configure: a =>
            a.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        var middle = SeedApplication(configure: a =>
            a.CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        var newest = SeedApplication(configure: a =>
            a.CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)
        );

        var response = await GetList();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadApplicationIds(response))
            .Should()
            .ContainInOrder(newest.ApplicationId, middle.ApplicationId, oldest.ApplicationId);
    }

    [Fact]
    public async Task GetList_WithEqualCreatedAt_BreaksTheTieByIdDescending()
    {
        Reset();
        var createdAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var idA = ObjectId.GenerateNewId();
        var idB = ObjectId.GenerateNewId();

        foreach (var id in new[] { idA, idB })
        {
            SeedApplication(configure: a =>
            {
                a.Id = id;
                a.CreatedAt = createdAt;
            });
        }

        var response = await GetList();

        var expected = new[] { idA, idB }
            .OrderByDescending(id => id)
            .Select(id => id.ToString())
            .ToArray();
        (await ReadApplicationIds(response)).Should().ContainInOrder(expected);
    }

    [Fact]
    public async Task GetList_IncludesWithdrawnApplications()
    {
        Reset();
        var withdrawn = SeedApplication(
            status: ApplicationStatus.Withdrawn,
            configure: a => a.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        );
        var live = SeedApplication(configure: a =>
            a.CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)
        );

        var response = await GetList();

        // Withdrawn records must stay visible — consumers need to render them.
        (await ReadApplicationIds(response))
            .Should()
            .ContainInOrder(live.ApplicationId, withdrawn.ApplicationId);
        var body = await response.Content.ReadFromJsonAsync<List<AccreditationApplicationModel>>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!
            .Select(a => a.ApplicationStatus)
            .Should()
            .BeEquivalentTo([ApplicationStatus.Saved, ApplicationStatus.Withdrawn]);
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

    [Fact]
    public async Task PatchTonnage_WhenQueriedAndPrnsSectionIsQueried_SucceedsAndKeepsQueriedStatus()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                // Both fields already populated from an earlier submission — reproduces the
                // resume-a-query scenario where a same-band re-save of tonnage alone must not
                // flip SectionStatus to Completed before the operator reaches the authorisers page.
                a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo1000;
                a.Prns.Authorisers =
                [
                    new PrnsAuthoriser { FullName = "Jane", Email = "jane@example.com" },
                ];
                a.Prns.SectionStatus = SectionStatus.Queried;
            }
        );

        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo1000 };
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

    [Fact]
    public async Task PatchTonnage_WhenWithdrawn_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Withdrawn);

        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/tonnage",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
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

    // --- Withdraw ---

    [Fact]
    public async Task Withdraw_SetsWithdrawnStatusAndPersistsReason()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Submitted);
        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new WithdrawResult(true)));

        var request = new WithdrawRequest
        {
            Reason = "No longer required",
            FullName = "Alex Withdrawer",
            Email = "alex.withdrawer@example.com",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        stored.WithdrawalReason.Should().Be("No longer required");
        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Is<QuerySubmitterContactDetails>(c =>
                    c.Email == "alex.withdrawer@example.com" && c.FullName == "Alex Withdrawer"
                ),
                "No longer required",
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Withdraw_FromQueried_Succeeds()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new WithdrawResult(true)));

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        body.Query!.QueriedSectionKeys.Should().BeEmpty();
        body.BusinessPlan.SectionStatus.Should().NotBe(SectionStatus.Queried);
    }

    [Fact]
    public async Task Withdraw_WhenAlreadyWithdrawn_ReturnsIdempotentOkWithoutCallingAdapter()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Withdrawn);

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Withdraw_WhenApproved_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Approved);

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Withdraw_WhenRejected_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Rejected);

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Started)]
    public async Task Withdraw_WhenNeverSubmittedDraft_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Withdraw_MissingReason_Returns400()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new WithdrawRequest { Reason = "" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Withdraw_ReasonOver200Words_Returns400()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var request = new WithdrawRequest
        {
            Reason = string.Join(' ', Enumerable.Repeat("word", 201)),
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Withdraw_WhenAdapterFails_ApplicationRemainsUnchanged()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Submitted);
        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new WithdrawResult(false)));

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Submitted);
        stored.WithdrawalReason.Should().BeNull();
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
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
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
    public async Task AddFile_WhenWithdrawn_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Withdrawn);

        var request = new FileUploadRequest
        {
            FileId = "file-004",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
            S3Key = "sampling-plans/file-004",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
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
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
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
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
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
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
            S3Key = "sampling-plans/file-new",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task AddFile_MissingDocumentType_Returns201AndPersistsNull()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest
        {
            FileId = "file-005",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            S3Key = "sampling-plans/file-005",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!
            .SamplingPlan.Files.Single(f => f.FileId == "file-005")
            .DocumentType.Should()
            .BeNull();
    }

    [Fact]
    public async Task AddFile_InvalidDocumentType_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new
        {
            FileId = "file-008",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            S3Key = "sampling-plans/file-008",
            DocumentType = 99,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddFile_SamplingPlanDocumentType_Returns201AndPersistsValue()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest
        {
            FileId = "file-006",
            Filename = "plan.pdf",
            ContentType = "application/pdf",
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
            S3Key = "sampling-plans/file-006",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!
            .SamplingPlan.Files.Single(f => f.FileId == "file-006")
            .DocumentType.Should()
            .Be(AccreditationFileDocumentType.SamplingPlan);
    }

    [Fact]
    public async Task AddFile_SupportingEvidenceDocumentType_Returns201AndPersistsValue()
    {
        Reset();
        var app = SeedApplication();

        var request = new FileUploadRequest
        {
            FileId = "file-007",
            Filename = "evidence.pdf",
            ContentType = "application/pdf",
            DocumentType = AccreditationFileDocumentType.SupportingEvidence,
            S3Key = "sampling-plans/file-007",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!
            .SamplingPlan.Files.Single(f => f.FileId == "file-007")
            .DocumentType.Should()
            .Be(AccreditationFileDocumentType.SupportingEvidence);
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

    // --- AddOverseasSite ---

    private static AddOverseasSiteRequest ValidAddOrsRequest() =>
        new()
        {
            OrsId = "001",
            SiteName = "Test Recycling GmbH",
            AddressLine1 = "Industriestrasse 42",
            TownOrCity = "Hamburg",
            Country = "Germany",
            ContactName = "Hans Müller",
            ContactEmail = "hans@testrecycling.de",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Rejected loads returned within 30 days at our expense.",
        };

    [Fact]
    public async Task AddOverseasSite_ValidRequest_Returns201WithNewSite()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.SiteName.Should().Be("Test Recycling GmbH");
        site.OrsId.Should().Be("001");
        site.Code1.Should().Be("A1181");
        site.SiteId.Should().Be(1);
    }

    [Fact]
    public async Task AddOverseasSite_DeriveIsEuAndIsOecd_CorrectlyClassifiesCountry()
    {
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { Country = "Germany" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.IsEu.Should().BeTrue();
        site.IsOecd.Should().BeTrue();
    }

    [Fact]
    public async Task AddOverseasSite_SiteIdIsOneMoreThanExistingMax()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel { SiteId = 900001, SiteName = "Existing Site 1" },
                    new OverseasSiteModel { SiteId = 900002, SiteName = "Existing Site 2" },
                ],
            }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.SiteId.Should().Be(900003);
    }

    [Fact]
    public async Task AddOverseasSite_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/nonexistent-id/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddOverseasSite_MissingSiteName_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new AddOverseasSiteRequest
        {
            OrsId = "001",
            SiteName = "",
            AddressLine1 = "Test St",
            TownOrCity = "Hamburg",
            Country = "Germany",
            ContactName = "Hans",
            ContactEmail = "hans@test.de",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Details here.",
        };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddOverseasSite_InvalidEmailFormat_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { ContactEmail = "not-an-email" };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddOverseasSite_InvalidBaselCode_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { Code1 = "INVALID" };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddOverseasSite_InvalidOperationCode_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { OperationCodes = ["R99"] };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(new[] { "R12" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R13" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R12", "R13" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R3", "R12" }, HttpStatusCode.Created)]
    [InlineData(new[] { "R4", "R13" }, HttpStatusCode.Created)]
    [InlineData(new[] { "R3", "R4", "R5", "R12", "R13" }, HttpStatusCode.Created)]
    public async Task AddOverseasSite_R12R13AccompanyingCodeRule_MatchesAc07Table(
        string[] operationCodes,
        HttpStatusCode expectedStatus
    )
    {
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { OperationCodes = [.. operationCodes] };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task AddOverseasSite_DuplicateOrsId_Returns409()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "Existing",
                        OrsId = "001",
                    },
                ],
            }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddOverseasSite_NonOecdEuCountry_IsOecdFalse()
    {
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { Country = "Bulgaria" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.IsEu.Should().BeTrue();
        site.IsOecd.Should().BeFalse();
    }

    [Fact]
    public async Task AddOverseasSite_WithLinkedWorkItem_NotifiesManagementBeOfNewOrsSite()
    {
        Reset();
        var app = SeedApplication(configure: a => a.CaseManagementWorkItemId = Guid.NewGuid());

        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .NotifySiteAddedAsync(
                Arg.Any<AccreditationApplicationModel>(),
                "ors",
                "001",
                null,
                true,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task AddOverseasSite_WithoutLinkedWorkItem_DoesNotNotifyManagementBe()
    {
        Reset();
        var app = SeedApplication();

        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .NotifySiteAddedAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>()
            );
    }

    // --- PromoteOverseasSite / RevertOverseasSite ---

    private static PromoteOverseasSiteRequest ValidPromoteRequest() =>
        new()
        {
            SiteName = "Promoted Recycling GmbH",
            AddressLine1 = "Neue Strasse 1",
            TownOrCity = "Munich",
            Country = "Germany",
            ContactName = "Greta Schmidt",
            ContactEmail = "greta@promotedrecycling.de",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Rejected loads returned within 30 days at our expense.",
        };

    private static OverseasSiteModel RegisteredOnlySite(int siteId = 900001) =>
        new()
        {
            SiteId = siteId,
            OrsId = "001",
            SiteName = "Registered Only Site",
            Country = "France",
            Selected = false,
            IsNewSite = false,
            RegisteredNowAccredited = false,
        };

    [Fact]
    public async Task PromoteOverseasSite_ValidRequest_OverwritesFieldsAndSetsFlags()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.SiteId.Should().Be(900001);
        site.OrsId.Should().Be("001");
        site.SiteName.Should().Be("Promoted Recycling GmbH");
        site.Selected.Should().BeTrue();
        site.RegisteredNowAccredited.Should().BeTrue();
    }

    [Fact]
    public async Task PromoteOverseasSite_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/nonexistent-id/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PromoteOverseasSite_SiteNotFound_Returns404()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [] }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PromoteOverseasSite_MissingSiteName_Returns400()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidPromoteRequest() with { SiteName = "" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(new[] { "R12" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R13" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R12", "R13" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R3", "R12" }, HttpStatusCode.OK)]
    [InlineData(new[] { "R4", "R13" }, HttpStatusCode.OK)]
    [InlineData(new[] { "R3", "R4", "R5", "R12", "R13" }, HttpStatusCode.OK)]
    public async Task PromoteOverseasSite_R12R13AccompanyingCodeRule_MatchesAc07Table(
        string[] operationCodes,
        HttpStatusCode expectedStatus
    )
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidPromoteRequest() with { OperationCodes = [.. operationCodes] };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(expectedStatus);
    }

    [Fact]
    public async Task PromoteOverseasSite_WhenQueriedAndOverseasSitesSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Queried;
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [RegisteredOnlySite()],
                };
            }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PromoteOverseasSite_Withdrawn_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Withdrawn,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [RegisteredOnlySite()],
                }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RevertOverseasSite_ValidRequest_RestoresSnapshotAndClearsFlags()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.SiteId.Should().Be(900001);
        site.SiteName.Should().Be("Registered Only Site");
        site.Selected.Should().BeFalse();
        site.RegisteredNowAccredited.Should().BeFalse();
    }

    [Fact]
    public async Task RevertOverseasSite_NotPreviouslyPromoted_Returns409()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RevertOverseasSite_SiteNotFound_Returns404()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [] }
        );

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevertOverseasSite_Withdrawn_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Withdrawn,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [RegisteredOnlySite()],
                }
        );

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- AddInterimSite ---

    private static AddInterimSiteRequest ValidAddInterimSiteRequest() =>
        new()
        {
            Country = "France",
            SiteName = "Interim Recycling Site",
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Jane Smith",
            ContactEmail = "jane.smith@example.com",
            ContactPhone = "+33 1 23 45 67 89",
        };

    [Fact]
    public async Task AddInterimSite_ValidRequest_Returns201WithNewInterimSite()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var interimSite = await response.Content.ReadFromJsonAsync<InterimSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        interimSite!.SiteName.Should().Be("Interim Recycling Site");
        interimSite.ContactPhone.Should().Be("+33 1 23 45 67 89");
        interimSite.IsNewSite.Should().BeTrue();
        interimSite.SiteId.Should().Be(2);
        interimSite.SiteNumber.Should().Be("SN-0002");
    }

    [Fact]
    public async Task AddInterimSite_SiteIdIsUniqueAcrossOrsAndInterimSites()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel { SiteId = 1, SiteName = "ORS 1" },
                    new OverseasSiteModel
                    {
                        SiteId = 5,
                        SiteName = "ORS 2",
                        InterimSite = new InterimSiteModel
                        {
                            SiteId = 12,
                            SiteNumber = "SN-0012",
                            Country = "Spain",
                            SiteName = "Existing Interim",
                            AddressLine1 = "1 Example Ave",
                            TownOrCity = "Madrid",
                            ContactName = "Existing Contact",
                            ContactEmail = "existing@example.com",
                            ContactPhone = "+34 123 456 789",
                        },
                    },
                ],
            }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var interimSite = await response.Content.ReadFromJsonAsync<InterimSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        interimSite!.SiteId.Should().Be(13);
        interimSite.SiteNumber.Should().Be("SN-0013");
    }

    [Fact]
    public async Task AddInterimSite_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/nonexistent-id/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddInterimSite_OverseasSiteNotFound_Returns404()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(siteId: 1);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/999/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddInterimSite_AlreadyHasInterimSite_Returns409()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "ORS 1",
                        InterimSite = new InterimSiteModel
                        {
                            SiteId = 2,
                            SiteNumber = "SN-0002",
                            Country = "Spain",
                            SiteName = "Existing Interim",
                            AddressLine1 = "1 Example Ave",
                            TownOrCity = "Madrid",
                            ContactName = "Existing Contact",
                            ContactEmail = "existing@example.com",
                            ContactPhone = "+34 123 456 789",
                        },
                    },
                ],
            }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddInterimSite_InvalidEmailFormat_Returns400()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = ValidAddInterimSiteRequest() with { ContactEmail = "not-an-email" };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddInterimSite_MissingContactPhone_Returns400()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = ValidAddInterimSiteRequest() with { ContactPhone = "" };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddInterimSite_WithLinkedWorkItem_NotifiesManagementBeOfNewInterimSite()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.CaseManagementWorkItemId = Guid.NewGuid();
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "ORS 1",
                        OrsId = "001",
                    },
                ],
            };
        });

        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .NotifySiteAddedAsync(
                Arg.Any<AccreditationApplicationModel>(),
                "interim",
                "001",
                "SN-0002",
                true,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task AddInterimSite_WithoutLinkedWorkItem_DoesNotNotifyManagementBe()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .NotifySiteAddedAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
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

    // --- QueryFromCaseManagement ---

    [Fact]
    public async Task QueryFromCaseManagement_ValidPush_SetsSectionStatusAndApplicationStatus()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new
        {
            queryNote = "Please clarify your business plan.",
            sectionKeys = new[] { "business-plan" },
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Queried);
        body.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Queried);
        body.Query!.QueryNote.Should().Be("Please clarify your business plan.");
    }

    [Fact]
    public async Task QueryFromCaseManagement_UnknownSectionKey_Returns400()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(configure: a => a.CaseManagementWorkItemId = workItemId);

        var request = new { queryNote = "note", sectionKeys = new[] { "not-a-real-key" } };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryFromCaseManagement_ExporterOnlyKeyForNonExporter_Returns400()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a =>
            {
                a.CaseManagementWorkItemId = workItemId;
                a.IsExporter = false;
            }
        );

        var request = new
        {
            queryNote = "note",
            sectionKeys = new[] { "overseas-reprocessing-sites" },
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QueryFromCaseManagement_ExporterOnlyKeyForExporter_Succeeds()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a =>
            {
                a.CaseManagementWorkItemId = workItemId;
                a.IsExporter = true;
            }
        );

        var request = new
        {
            queryNote = "note",
            sectionKeys = new[] { "overseas-reprocessing-sites" },
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueryFromCaseManagement_AuthorityToIssueAndPrnTonnage_CollapseOntoPrnsOnce()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        var app = SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new
        {
            queryNote = "note",
            sectionKeys = new[] { "authority-to-issue", "prn-tonnage" },
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.Prns.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    [Fact]
    public async Task QueryFromCaseManagement_UnknownWorkItem_Returns404()
    {
        Reset();
        var request = new { queryNote = "note", sectionKeys = new[] { "business-plan" } };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{Guid.NewGuid()}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task QueryFromCaseManagement_AlreadyQueried_Returns409WithoutOverwritingQueriedSectionKeys()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = workItemId;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "Original query",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );

        var request = new
        {
            queryNote = "Second query while first is open",
            sectionKeys = new[] { "sampling-and-inspection-plan" },
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var stored = await _factory.FakePersistence.GetByCaseManagementWorkItemIdAsync(workItemId);
        stored!.Query!.QueryNote.Should().Be("Original query");
        stored.Query.QueriedSectionKeys.Should().BeEquivalentTo(["business-plan"]);
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Started)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task QueryFromCaseManagement_IllegalStatus_Returns409(ApplicationStatus status)
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(status: status, configure: a => a.CaseManagementWorkItemId = workItemId);

        var request = new { queryNote = "note", sectionKeys = new[] { "business-plan" } };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task QueryFromCaseManagement_UpdatedStatus_Succeeds()
    {
        // Updated = a prior query was already resolved via resubmit; CM must be able to raise
        // a fresh query against the same application.
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Updated,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new { queryNote = "note", sectionKeys = new[] { "business-plan" } };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Section edit gate ---

    [Fact]
    public async Task PatchPrns_WhenQueriedAndPrnsSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.BusinessPlan.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchPrnsRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchPrns_WhenQueriedAndPrnsSectionIsQueried_SucceedsAndKeepsQueriedStatus()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchPrnsRequest
        {
            PlannedTonnageBand = PlannedTonnageBand.UpTo500,
            Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "jane@example.com" }],
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

        // SectionStatus must stay Queried while ApplicationStatus == Queried — only Resubmit
        // recomputes it. Otherwise a partial PATCH prematurely clears the query marker before
        // the operator has finished responding across every field the section covers.
        body!.Prns.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    [Fact]
    public async Task AddBesEvidenceFile_WhenQueriedAndBesEvidenceSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Test Site" }],
                }
        );

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

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task InitiateUpload_WhenQueriedAndSamplingPlanSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Queried);

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

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_SettingQueriedDirectly_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new PatchBesEvidenceSectionRequest { SectionStatus = SectionStatus.Queried };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // The gate itself (IsSectionEditable) is exhaustively unit-tested in
    // AccreditationApplicationSectionsTests. The tests below exist to prove each endpoint is
    // actually wired to it — PatchPrns/PatchTonnage/AddBesEvidenceFile/InitiateUpload above cover
    // three of the ten gated call sites; everything below was previously untested at the endpoint
    // level, leaving the wiring (not just the shared helper) unverified.

    [Fact]
    public async Task PatchBusinessPlan_WhenQueriedAndBusinessPlanSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchBusinessPlanRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchSamplingPlan_WhenQueriedAndSamplingPlanSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchSamplingPlanRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchOverseasSites_WhenQueriedAndOverseasSitesSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchOverseasSitesRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchBesEvidence_WhenQueriedAndBesEvidenceSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var request = new PatchBesEvidenceRequest { DoYouWantToUploadMoreEvidence = true };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_WhenQueriedAndBesEvidenceSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files/bes-file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task InitiateBesEvidenceUpload_WhenQueriedAndBesEvidenceSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var request = new
        {
            redirectUrl = "http://frontend/redirect",
            s3Bucket = "test-bucket",
            s3Path = "uploads/test.pdf",
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/bes-evidence/initiate",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_WhenQueriedAndBesEvidenceSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        // Completed (not Queried) so this clears the validator and reaches the gate check itself —
        // distinct from PatchBesEvidenceSection_SettingQueriedDirectly_Returns400 above, which
        // never gets past the validator.
        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- Resubmit ---

    [Fact]
    public async Task Resubmit_WhenNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Started);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Resubmit_WhenAlreadyUpdated_ReturnsIdempotentOk()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.Updated);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Resubmit_AdapterFails_Returns502AndApplicationRemainsQueried()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.BusinessPlan.SectionStatus = SectionStatus.Queried;
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new ResumeFromQueryResult(false)));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Queried);
    }

    [Fact]
    public async Task Resubmit_Success_TransitionsToUpdatedAndAppendsQuerySubmission()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.BusinessPlan.SectionStatus = SectionStatus.Queried; // untouched by operator
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify",
                    QueriedSectionKeys = ["business-plan"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new ResumeFromQueryResult(true)));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Updated);
        // Untouched (still-Queried) section is force-reset to its computed status.
        body.BusinessPlan.SectionStatus.Should().Be(SectionStatus.NotStarted);
        body.Query!.QueriedSectionKeys.Should().BeEmpty();
        body.Query.QuerySubmissions.Should().ContainSingle();
        body.Query.QuerySubmissions[0].QuerySubmitterContactDetails.FullName.Should().Be("Jane");
        body.Query.QueryNote.Should().Be("clarify");
    }

    [Fact]
    public async Task Resubmit_SectionWithRealData_RecomputesToNonTrivialStatusNotJustNotStarted()
    {
        // The other Resubmit-success test only proves the recompute branch resolves an *empty*
        // section to NotStarted — that would still pass even if ComputeCurrentStatus were broken
        // (e.g. always returned NotStarted). This seeds Prns with real data so only a correct
        // ComputePrns-equivalent calculation can produce Completed.
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.Prns.SectionStatus = SectionStatus.Queried;
                a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo500;
                a.Prns.Authorisers =
                [
                    new PrnsAuthoriser { FullName = "Jane", Email = "jane@example.com" },
                ];
                a.Query = new AccreditationApplicationQuery
                {
                    QueryNote = "clarify tonnage",
                    QueriedSectionKeys = ["authority-to-issue", "prn-tonnage"],
                };
            }
        );
        _factory
            .MockCaseWorkingAdapter.ResumeFromQueryAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new ResumeFromQueryResult(true)));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/resubmit",
            new ResubmitRequest
            {
                FullName = "Jane",
                Email = "jane@example.com",
                Role = "Manager",
            },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Prns.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    // --- StatusChangedFromCaseManagement ---

    [Theory]
    [InlineData("submitted", ApplicationStatus.Submitted)]
    [InlineData("duly-made", ApplicationStatus.DulyMade)]
    [InlineData("updated", ApplicationStatus.Updated)]
    public async Task StatusChangedFromCaseManagement_MappedState_SetsApplicationStatus(
        string toStateId,
        ApplicationStatus expectedStatus
    )
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = toStateId,
            ActionId = "some-action",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(expectedStatus);
    }

    [Theory]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.Updated)]
    [InlineData(ApplicationStatus.DulyMade)]
    public async Task StatusChangedFromCaseManagement_Approved_FromLegalStatus_Succeeds(
        ApplicationStatus fromStatus
    )
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: fromStatus,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "approved",
            ActionId = "approve",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
    }

    [Fact]
    public async Task StatusChangedFromCaseManagement_Rejected_FromLegalStatus_Succeeds()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "rejected",
            ActionId = "reject",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Rejected);
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Started)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task StatusChangedFromCaseManagement_ApproveFromIllegalStatus_Returns409(
        ApplicationStatus status
    )
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(status: status, configure: a => a.CaseManagementWorkItemId = workItemId);

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "approved",
            ActionId = "approve",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task StatusChangedFromCaseManagement_UnknownWorkItem_Returns404()
    {
        Reset();
        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "submitted",
            ActionId = "submit",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{Guid.NewGuid()}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StatusChangedFromCaseManagement_OutOfOrderDelivery_IsNoOp()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        var lastUpdated = DateTime.UtcNow;
        var app = SeedApplication(
            status: ApplicationStatus.Updated,
            configure: a =>
            {
                a.CaseManagementWorkItemId = workItemId;
                a.CaseManagementStatusUpdatedAt = lastUpdated;
            }
        );

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "duly-made",
            ActionId = "duly-made-transition",
            OccurredAt = lastUpdated.AddSeconds(-1),
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Updated);
    }

    [Fact]
    public async Task StatusChangedFromCaseManagement_ContinueReviewDuringDulyMaking_DowngradesFromUpdatedToSubmitted()
    {
        // Mirrors CM's continue-review-during-duly-making action, which can push CM (and
        // therefore OJ) back to 'submitted' from 'updated' — ordering is timestamp-based, not
        // state-precedence-based (RA-368 §4.3).
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Updated,
            configure: a =>
            {
                a.CaseManagementWorkItemId = workItemId;
                a.CaseManagementStatusUpdatedAt = DateTime.UtcNow.AddMinutes(-5);
            }
        );

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "submitted",
            ActionId = "continue-review-during-duly-making",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.Submitted);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn, "duly-made")]
    [InlineData(ApplicationStatus.Withdrawn, "submitted")]
    [InlineData(ApplicationStatus.Approved, "duly-made")]
    [InlineData(ApplicationStatus.Rejected, "updated")]
    public async Task StatusChangedFromCaseManagement_MappedPushFromTerminalStatus_Returns409AndLeavesStatusUnchanged(
        ApplicationStatus fromStatus,
        string toStateId
    )
    {
        // A withdrawn/approved/rejected application must stay terminal even for pushes that
        // aren't themselves "approved"/"rejected" — otherwise a duly-made push could reopen a
        // withdrawn application's withdraw/query/approve paths (RA-368 review).
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: fromStatus,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = toStateId,
            ActionId = "some-action",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var stored = await _factory.FakePersistence.GetByCaseManagementWorkItemIdAsync(workItemId);
        stored!.ApplicationStatus.Should().Be(fromStatus);
    }

    [Theory]
    [InlineData("assessment-in-progress")]
    [InlineData("awaiting-decision")]
    public async Task StatusChangedFromCaseManagement_UnmappedState_LeavesApplicationStatusUnchanged(
        string toStateId
    )
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.DulyMade,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = toStateId,
            ActionId = "payment-received",
            OccurredAt = DateTime.UtcNow,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.ApplicationStatus.Should().Be(ApplicationStatus.DulyMade);
    }

    [Fact]
    public async Task StatusChangedFromCaseManagement_PushOlderThanQuery_IsIgnoredAndLeavesQueriedStatus()
    {
        // QueryFromCaseManagement stamps CaseManagementStatusUpdatedAt so it shares one ordering
        // watermark with StatusChangedFromCaseManagement (RA-368 review) — a status push whose
        // OccurredAt predates the query must not silently clobber the open Queried status.
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.Submitted,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var queryRequest = new { queryNote = "note", sectionKeys = new[] { "business-plan" } };
        var queryResponse = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            queryRequest,
            cancellationToken: TestContext.Current.CancellationToken
        );
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var statusRequest = new StatusChangedFromCaseManagementRequest
        {
            ToStateId = "duly-made",
            ActionId = "duly-made-transition",
            OccurredAt = DateTime.UtcNow.AddMinutes(-5),
        };
        var statusResponse = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/status",
            statusRequest,
            cancellationToken: TestContext.Current.CancellationToken
        );

        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByCaseManagementWorkItemIdAsync(workItemId);
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Queried);
    }

    // --- Widened legality gates (query/withdraw now also allow DulyMade) ---

    [Fact]
    public async Task QueryFromCaseManagement_DulyMadeStatus_Succeeds()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.DulyMade,
            configure: a => a.CaseManagementWorkItemId = workItemId
        );

        var request = new { queryNote = "note", sectionKeys = new[] { "business-plan" } };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/case-management/{workItemId}/query",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Withdraw_FromDulyMade_Succeeds()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.DulyMade);
        _factory
            .MockCaseWorkingAdapter.WithdrawApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new WithdrawResult(true)));

        var request = new WithdrawRequest { Reason = "No longer required" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/withdraw",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
