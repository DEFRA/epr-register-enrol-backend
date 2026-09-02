using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        _factory.FakePendingUploadService.Clear();
        _factory.FakeCaseManagementAuthNonceStore.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
        _factory.MockAuditPersistence.ClearSubstitute(ClearOptions.All);
    }

    // Simulates a real CDP-uploader webhook callback having already completed for
    // fileUploadId, so AddFile/AddBesEvidenceFile can resolve it via the real
    // IPendingUploadService singleton instead of trusting client-supplied file fields.
    private async Task<string> SeedValidatedUpload(
        string fileId,
        string filename,
        string s3Key,
        string? contentType = "application/pdf",
        string? s3Bucket = "test-bucket",
        string fileStatus = "complete"
    )
    {
        var fileUploadId = $"upload-{fileId}";
        var pendingUploadService = _factory.Services.GetRequiredService<IPendingUploadService>();
        await pendingUploadService.CreateAsync(fileUploadId, "https://cdp.example/status");
        await pendingUploadService.CompleteAsync(
            fileUploadId,
            new CdpCallbackFile
            {
                FileId = fileId,
                Filename = filename,
                FileStatus = fileStatus,
                ContentType = contentType,
                S3Key = s3Key,
                S3Bucket = s3Bucket,
            }
        );
        return fileUploadId;
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
                            CompanyRegisteredAddress = "29 Acacia Road, London, SW1A 1AA",
                            OverseasSites = [],
                            Prns = new ReExPrnsDto
                            {
                                PlannedTonnageBand = PlannedTonnageBand.UpTo5000,
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
        body.Prns.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo5000);
        body.CompanyRegisteredAddress.Should().Be("29 Acacia Road, London, SW1A 1AA");
    }

    // RA-456: ReEx already sends the "Activities or investment not covered by the other
    // categories" usage description and HttpReExApiAdapter already maps it into
    // ReExBusinessPlanDto.OtherPercent (see HttpReExApiAdapterTests), but Seed used to silently
    // drop it when copying ReExBusinessPlanDto onto the domain AccreditationApplicationBusinessPlan
    // — this proves OtherPercent now survives all the way into the persisted domain model.
    [Fact]
    public async Task Seed_WithPriorYearData_CarriesOtherPercentIntoDomainModel()
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
                            BusinessPlan = new ReExBusinessPlanDto { OtherPercent = 50 },
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
        body!.BusinessPlan.OtherPercent.Should().Be(50);
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

    [Theory]
    [InlineData("plastic")]
    [InlineData("PLASTIC")]
    [InlineData("PlAsTiC")]
    public async Task Seed_MaterialTypeCasingVariant_Returns201WithApplication(
        string materialTypeSegment
    )
    {
        Reset();
        _factory
            .MockReExAdapter.GetAccreditationAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<MaterialType>(),
                Arg.Any<int>()
            )
            .Returns(Task.FromResult(MinimalAdapterSuccess(material: MaterialType.Plastic)));

        var request = new SeedRequest { Year = 2026 };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/reg-1/{materialTypeSegment}/seed",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.MaterialType.Should().Be(MaterialType.Plastic);
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
                            CompaniesHouseNumber = "01234567",
                            PermitNumbers = ["WML123456", "PPC456789"],
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
        body.CompaniesHouseNumber.Should().Be("01234567");
        body.PermitNumbers.Should().BeEquivalentTo(["WML123456", "PPC456789"]);
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
            .Returns(Task.FromResult(new NotificationStatusResult("failed", null)));

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
    public async Task GetById_LinkedWorkItem_ReturnsAdapterSlaDueDate()
    {
        Reset();
        var app = SeedApplication(configure: a => a.CaseManagementWorkItemId = Guid.NewGuid());
        var slaDueDate = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        _factory
            .MockCaseWorkingAdapter.GetNotificationStatusAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new NotificationStatusResult(null, slaDueDate)));

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.DueDate.Should().Be(slaDueDate);
    }

    [Fact]
    public async Task GetById_NoLinkedWorkItem_NotificationStatusAndDueDateAreNull()
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
        body.DueDate.Should().BeNull();
        await _factory
            .MockCaseWorkingAdapter.DidNotReceive()
            .GetNotificationStatusAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task GetById_AdapterThrows_Returns200WithNullNotificationStatusAndDueDate()
    {
        Reset();
        var app = SeedApplication(configure: a => a.CaseManagementWorkItemId = Guid.NewGuid());
        _factory
            .MockCaseWorkingAdapter.GetNotificationStatusAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                Task.FromException<NotificationStatusResult>(
                    new HttpRequestException("unreachable")
                )
            );

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
        body.DueDate.Should().BeNull();
    }

    // RA-503: GetById backfills OrgId (ReEx's numeric organisation number) for an application
    // read before Submit's own resolution ran, and persists the result so later reads skip the
    // ReEx round trip entirely.
    [Fact]
    public async Task GetById_OrgIdNotYetResolved_BackfillsFromReExAndPersists()
    {
        Reset();
        var app = SeedApplication(configure: a => a.OrgId = null);
        _factory
            .MockReExAdapter.GetOrganisationNumberAsync("org-123", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReExResult<int?>.Success(500500, 200)));

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OrgId.Should().Be(500500);

        // Persisted, not just returned once: a second read must show the same value even if
        // ReEx would now return something different (proving GetById skips ReEx once resolved).
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory
            .MockReExAdapter.GetOrganisationNumberAsync("org-123", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReExResult<int?>.Success(999999, 200)));
        var second = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondBody = await second.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        secondBody!.OrgId.Should().Be(500500);
    }

    [Fact]
    public async Task GetById_OrgIdAlreadyResolved_DoesNotCallReEx()
    {
        Reset();
        var app = SeedApplication(configure: a => a.OrgId = 500500);

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OrgId.Should().Be(500500);
        await _factory
            .MockReExAdapter.DidNotReceive()
            .GetOrganisationNumberAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetById_ReExLookupFails_OrgIdStaysNullAndIsNotPermanentlyCached()
    {
        Reset();
        var app = SeedApplication(configure: a => a.OrgId = null);
        _factory
            .MockReExAdapter.GetOrganisationNumberAsync("org-123", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    ReExResult<int?>.Fail(new ReExError(ReExErrorKind.ServerError, "down"), 500)
                )
            );

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OrgId.Should().BeNull();

        // A failure must not be cached as a permanent null: the next read retries ReEx.
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory
            .MockReExAdapter.GetOrganisationNumberAsync("org-123", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReExResult<int?>.Success(500500, 200)));
        var second = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondBody = await second.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        secondBody!.OrgId.Should().Be(500500);
    }

    // RA-526: GetById backfills Nation for an application read before Seed's own resolution ran,
    // and persists the result so later reads skip the ReEx round trip entirely. Mirrors the
    // RA-503 OrgId backfill trio above.
    [Fact]
    public async Task GetById_NationNotYetResolved_BackfillsFromReExAndPersists()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.Nation = null;
            a.RegistrationId = "reg-1";
        });
        _factory
            .MockReExAdapter.GetNationAsync("org-123", "reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReExResult<Nation>.Success(Nation.Wales, 200)));

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Nation.Should().Be(Nation.Wales);

        // Persisted, not just returned once: a second read must show the same value even if
        // ReEx would now return something different (proving GetById skips ReEx once resolved).
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory
            .MockReExAdapter.GetNationAsync("org-123", "reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReExResult<Nation>.Success(Nation.Scotland, 200)));
        var second = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondBody = await second.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        secondBody!.Nation.Should().Be(Nation.Wales);
    }

    [Fact]
    public async Task GetById_NationAlreadyResolved_DoesNotCallReEx()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.Nation = Nation.NorthernIreland;
            a.RegistrationId = "reg-1";
        });

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Nation.Should().Be(Nation.NorthernIreland);
        await _factory
            .MockReExAdapter.DidNotReceive()
            .GetNationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetById_NoRegistrationId_DoesNotCallReExForNation()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.Nation = null;
            a.RegistrationId = null;
        });

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Nation.Should().BeNull();
        await _factory
            .MockReExAdapter.DidNotReceive()
            .GetNationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetById_NationReExLookupFails_NationStaysNullAndIsNotPermanentlyCached()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.Nation = null;
            a.RegistrationId = "reg-1";
        });
        _factory
            .MockReExAdapter.GetNationAsync("org-123", "reg-1", Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult(
                    ReExResult<Nation>.Fail(new ReExError(ReExErrorKind.ServerError, "down"), 500)
                )
            );

        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Nation.Should().BeNull();

        // A failure must not be cached as a permanent null: the next read retries ReEx.
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory
            .MockReExAdapter.GetNationAsync("org-123", "reg-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReExResult<Nation>.Success(Nation.Wales, 200)));
        var second = await _client.GetAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}",
            cancellationToken: TestContext.Current.CancellationToken
        );
        var secondBody = await second.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        secondBody!.Nation.Should().Be(Nation.Wales);
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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchPrns_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var request = new PatchPrnsRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo500 };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/prns",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- RA-292 AC03: authority-to-issue isNew derivation ---

    private static PrnsAuthoriser Authoriser(string fullName, string email, bool isNew = false) =>
        new()
        {
            FullName = fullName,
            Email = email,
            IsNew = isNew,
        };

    private async Task<List<PrnsAuthoriser>> PatchAuthorisers(string url, object request)
    {
        var response = await _client.PatchAsJsonAsync(
            url,
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        return body!.Prns.Authorisers;
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_NewEmail_IsFlaggedNewAndKnownEmailIsNot(string route)
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.Prns.Authorisers = [Authoriser("Prior Year Person", "prior@example.com")]
        );

        var authorisers = await PatchAuthorisers(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}",
            new
            {
                authorisers = new[]
                {
                    new { fullName = "Prior Year Person", email = "prior@example.com" },
                    new { fullName = "Added Now", email = "added@example.com" },
                },
            }
        );

        authorisers.Should().HaveCount(2);
        authorisers[0].IsNew.Should().BeFalse();
        authorisers[1].IsNew.Should().BeTrue();
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_ClientSendsNoIsNewKey_ServerStillDerivesIt(string route)
    {
        // The operator frontend is not required to send isNew at all — the flag the regulator
        // sees must be right regardless of what the client does with the field.
        Reset();
        var app = SeedApplication();

        var authorisers = await PatchAuthorisers(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}",
            new { authorisers = new[] { new { fullName = "Added Now", email = "a@example.com" } } }
        );

        authorisers.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_ClientSendsIsNewFalse_CannotClearTheFlag(string route)
    {
        Reset();
        var app = SeedApplication();

        var authorisers = await PatchAuthorisers(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}",
            new
            {
                authorisers = new[]
                {
                    new
                    {
                        fullName = "Added Now",
                        email = "a@example.com",
                        isNew = false,
                    },
                },
            }
        );

        authorisers.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_ClientSendsIsNewTrueForKnownEmail_CannotSetTheFlag(
        string route
    )
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.Prns.Authorisers = [Authoriser("Known", "known@example.com")]
        );

        var authorisers = await PatchAuthorisers(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}",
            new
            {
                authorisers = new[]
                {
                    new
                    {
                        fullName = "Known",
                        email = "known@example.com",
                        isNew = true,
                    },
                },
            }
        );

        authorisers.Should().ContainSingle().Which.IsNew.Should().BeFalse();
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_EmailDiffersByCaseAndWhitespace_StaysNotNew(string route)
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.Prns.Authorisers = [Authoriser("Known", "known@example.com")]
        );

        var authorisers = await PatchAuthorisers(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}",
            new
            {
                authorisers = new[] { new { fullName = "Known", email = "  KNOWN@Example.COM " } },
            }
        );

        authorisers.Should().ContainSingle().Which.IsNew.Should().BeFalse();
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_AlreadyFlaggedNew_StaysNewAcrossRepeatedSaves(string route)
    {
        Reset();
        var app = SeedApplication();
        var url = $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}";
        var body = new
        {
            authorisers = new[] { new { fullName = "Added Now", email = "a@example.com" } },
        };

        await PatchAuthorisers(url, body);
        var authorisers = await PatchAuthorisers(url, body);

        authorisers.Should().ContainSingle().Which.IsNew.Should().BeTrue();
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_OmittedAuthoriser_IsRemovedNotResurrected(string route)
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.Prns.Authorisers = [
                Authoriser("Kept", "kept@example.com"),
                Authoriser("Removed", "removed@example.com"),
            ]
        );

        var authorisers = await PatchAuthorisers(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}",
            new { authorisers = new[] { new { fullName = "Kept", email = "kept@example.com" } } }
        );

        authorisers.Should().ContainSingle().Which.Email.Should().Be("kept@example.com");
    }

    [Theory]
    [InlineData("prns")]
    [InlineData("tonnage")]
    public async Task PatchAuthorisers_RequestOmitsAuthorisers_LeavesPersistedListUntouched(
        string route
    )
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.Prns.Authorisers = [Authoriser("Known", "known@example.com", true)]
        );

        var authorisers = await PatchAuthorisers(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/{route}",
            new { plannedTonnageBand = "UpTo500" }
        );

        authorisers.Should().ContainSingle();
        authorisers[0].Email.Should().Be("known@example.com");
        authorisers[0].IsNew.Should().BeTrue();
    }

    [Fact]
    public async Task Seed_PriorYearAuthorisers_AreNotFlaggedNew()
    {
        // AC03 hinges on this: contacts carried over from last year's accreditation existed
        // before the application, so the regulator must not see them badged as new.
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
                                PlannedTonnageBand = PlannedTonnageBand.UpTo5000,
                                Authorisers =
                                [
                                    // isNew: true here stands in for a stray value arriving from
                                    // ReEx — carrying it over would misinform the regulator.
                                    Authoriser("Prior Year Person", "prior@example.com", true),
                                ],
                            },
                        },
                        200
                    )
                )
            );

        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/reg-1/Steel/seed",
            new SeedRequest { Year = 2026 },
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!
            .Prns.Authorisers.Should()
            .ContainSingle()
            .Which.Should()
            .Match<PrnsAuthoriser>(a => a.Email == "prior@example.com" && !a.IsNew);
    }

    // --- RA-292 AC01/AC02: isNewSite is server-owned across PatchOverseasSites ---

    private async Task<List<OverseasSiteModel>> PatchSites(
        AccreditationApplicationModel app,
        object request
    )
    {
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
        return body!.OverseasSites!.Sites;
    }

    private AccreditationApplicationModel SeedApplicationWithSites(
        params OverseasSiteModel[] sites
    ) =>
        SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [.. sites] }
        );

    [Fact]
    public async Task PatchOverseasSites_ClientOmitsIsNewSite_DoesNotFlipRegisteredSiteToNew()
    {
        // The live risk: the frontend builds the PATCH body by spreading sites off the GET, so a
        // single dropped key used to relabel every site as new.
        Reset();
        var app = SeedApplicationWithSites(
            new OverseasSiteModel
            {
                SiteId = 1,
                SiteName = "ReEx Registered Site",
                IsNewSite = false,
            }
        );

        var sites = await PatchSites(
            app,
            new { sites = new[] { new { siteId = 1, siteName = "ReEx Registered Site" } } }
        );

        sites.Should().ContainSingle().Which.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public async Task PatchOverseasSites_ClientClaimsNewForKnownSite_CannotSetTheFlag()
    {
        Reset();
        var app = SeedApplicationWithSites(
            new OverseasSiteModel
            {
                SiteId = 1,
                SiteName = "Registered",
                IsNewSite = false,
            }
        );

        var sites = await PatchSites(
            app,
            new
            {
                sites = new[]
                {
                    new
                    {
                        siteId = 1,
                        siteName = "Registered",
                        isNewSite = true,
                    },
                },
            }
        );

        sites.Should().ContainSingle().Which.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public async Task PatchOverseasSites_ClientClaimsNotNewForGenuinelyNewSite_CannotClearTheFlag()
    {
        Reset();
        var app = SeedApplicationWithSites(
            new OverseasSiteModel
            {
                SiteId = 1,
                SiteName = "Operator Added",
                IsNewSite = true,
            }
        );

        var sites = await PatchSites(
            app,
            new
            {
                sites = new[]
                {
                    new
                    {
                        siteId = 1,
                        siteName = "Operator Added",
                        isNewSite = false,
                    },
                },
            }
        );

        sites.Should().ContainSingle().Which.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public async Task PatchOverseasSites_GenuinelyNewSiteSurvivesRepeatedSaves()
    {
        Reset();
        var app = SeedApplicationWithSites(
            new OverseasSiteModel
            {
                SiteId = 1,
                SiteName = "Operator Added",
                IsNewSite = true,
            }
        );
        var body = new { sites = new[] { new { siteId = 1, siteName = "Operator Added" } } };

        await PatchSites(app, body);
        var sites = await PatchSites(app, body);

        sites.Should().ContainSingle().Which.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public async Task PatchOverseasSites_PreservesInterimSiteIsNewSite()
    {
        Reset();
        var app = SeedApplicationWithSites(
            new OverseasSiteModel
            {
                SiteId = 1,
                SiteName = "Site",
                IsNewSite = false,
                InterimSite = new InterimSiteModel
                {
                    SiteId = 2,
                    SiteNumber = "SN-0002",
                    Country = "France",
                    SiteName = "Interim",
                    AddressLine1 = "1 Rue Example",
                    TownOrCity = "Paris",
                    ContactName = "Marie Curie",
                    ContactEmail = "marie@example.com",
                    ContactPhone = "0033111222333",
                    IsNewSite = true,
                },
            }
        );

        var sites = await PatchSites(
            app,
            new
            {
                sites = new[]
                {
                    new
                    {
                        siteId = 1,
                        siteName = "Site",
                        isNewSite = true,
                        interimSite = new
                        {
                            siteId = 2,
                            siteNumber = "SN-0002",
                            country = "France",
                            siteName = "Interim",
                            addressLine1 = "1 Rue Example",
                            townOrCity = "Paris",
                            contactName = "Marie Curie",
                            contactEmail = "marie@example.com",
                            contactPhone = "0033111222333",
                            isNewSite = false,
                        },
                    },
                },
            }
        );

        sites.Should().ContainSingle();
        sites[0].IsNewSite.Should().BeFalse("the ORS flag is server-owned");
        sites[0].InterimSite!.IsNewSite.Should().BeTrue("the interim flag is server-owned too");
    }

    [Fact]
    public async Task AddOverseasSite_ThenPatch_SiteStaysFlaggedNew()
    {
        // End to end over the two endpoints that actually matter for AC01: the add endpoint is the
        // only place newness is switched on, and a later save must not undo it.
        Reset();
        var app = SeedApplication();

        var added = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        added.StatusCode.Should().Be(HttpStatusCode.Created);

        var sites = await PatchSites(
            app,
            new { sites = new[] { new { siteId = 1, siteName = "Test Recycling GmbH" } } }
        );

        sites.Should().ContainSingle().Which.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public async Task PatchOverseasSites_PromotedSite_KeepsItsRevertTargetIntact()
    {
        // Distinct from the isNewSite work: PreviousSites is [JsonIgnore], so the undo stack never
        // reaches the frontend and cannot come back on a PATCH. Before OverseasSiteMerge carried
        // it across, any save of the site list destroyed a promoted site's revert target and the
        // subsequent revert failed with a 409.
        Reset();
        var app = SeedApplicationWithSites(RegisteredOnlySite());

        var promoted = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        promoted.StatusCode.Should().Be(HttpStatusCode.OK);

        // An ordinary save of the site list. registeredNowAccredited is included here so this test
        // stays focused on the undo stack; the sibling test below omits it deliberately.
        await PatchSites(
            app,
            new
            {
                sites = new[]
                {
                    new
                    {
                        siteId = 900001,
                        siteName = "Promoted Recycling GmbH",
                        registeredNowAccredited = true,
                    },
                },
            }
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
        site!.SiteName.Should().Be("Registered Only Site", "the pre-promotion values are restored");
        site.RegisteredNowAccredited.Should().BeFalse();
    }

    [Fact]
    public async Task PatchOverseasSites_OmittingRegisteredNowAccredited_DoesNotUnPromoteTheSite()
    {
        // epr-zgrb, found empirically while writing the sibling test above: registeredNowAccredited
        // is serialised, so it survived only while the frontend happened to echo it back. A body
        // that omits the key deserialised it to false, silently clearing the promotion — and the
        // revert then failed on the promote-flag guard rather than the undo-stack guard, i.e. a
        // user-visible broken journey rather than a stale flag.
        Reset();
        var app = SeedApplicationWithSites(RegisteredOnlySite());

        var promoted = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );
        promoted.StatusCode.Should().Be(HttpStatusCode.OK);

        // Deliberately omits registeredNowAccredited — this is the exact body that used to break it.
        var sites = await PatchSites(
            app,
            new { sites = new[] { new { siteId = 900001, siteName = "Promoted Recycling GmbH" } } }
        );

        sites.Should().ContainSingle().Which.RegisteredNowAccredited.Should().BeTrue();

        // And the journey still works end to end, which is what the operator would actually notice.
        var reverted = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        reverted.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = await reverted.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.SiteName.Should().Be("Registered Only Site");
        site.RegisteredNowAccredited.Should().BeFalse();
    }

    [Fact]
    public async Task PatchOverseasSites_OmittingOrsId_PreservesIt()
    {
        Reset();
        var app = SeedApplicationWithSites(
            new OverseasSiteModel
            {
                SiteId = 1,
                SiteName = "Operator Added",
                OrsId = "001",
            }
        );

        var sites = await PatchSites(
            app,
            new { sites = new[] { new { siteId = 1, siteName = "Operator Added" } } }
        );

        sites.Should().ContainSingle().Which.OrsId.Should().Be("001");
    }

    [Fact]
    public async Task PatchOverseasSites_KnownSiteWithNoPersistedOrsId_ClientCannotInventOne()
    {
        // RA-507: a site with no persisted OrsId (e.g. a legacy document predating
        // HttpReExApiAdapter populating it) is still server-owned once persisted — a client
        // supplying one on PATCH must not be able to set it.
        Reset();
        var app = SeedApplicationWithSites(
            new OverseasSiteModel { SiteId = 1, SiteName = "ReEx Registered Site" }
        );

        var sites = await PatchSites(
            app,
            new
            {
                sites = new[]
                {
                    new
                    {
                        siteId = 1,
                        siteName = "ReEx Registered Site",
                        orsId = "001",
                    },
                },
            }
        );

        sites.Should().ContainSingle().Which.OrsId.Should().BeNull();
    }

    [Fact]
    public async Task PatchOverseasSites_ClientClaimsPromotedForAnUnpromotedSite_CannotSetTheFlag()
    {
        Reset();
        var app = SeedApplicationWithSites(RegisteredOnlySite());

        var sites = await PatchSites(
            app,
            new
            {
                sites = new[]
                {
                    new
                    {
                        siteId = 900001,
                        siteName = "Registered Only Site",
                        registeredNowAccredited = true,
                    },
                },
            }
        );

        sites.Should().ContainSingle().Which.RegisteredNowAccredited.Should().BeFalse();
    }

    [Fact]
    public async Task AddOverseasSite_FlagsTheSiteAsNew()
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
        site!.IsNewSite.Should().BeTrue();
    }

    // --- PatchTonnage ---

    [Fact]
    public async Task PatchTonnage_WithAuthorisersOnly_UpdatesAuthorisersLeavesTonnageBandUnchanged()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Saved,
            configure: a => a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo5000
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
        body!.Prns.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo5000);
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
                a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo5000;
                a.Prns.Authorisers =
                [
                    new PrnsAuthoriser { FullName = "Jane", Email = "jane@example.com" },
                ];
                a.Prns.SectionStatus = SectionStatus.Queried;
            }
        );

        var request = new PatchTonnageRequest { PlannedTonnageBand = PlannedTonnageBand.UpTo5000 };
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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchTonnage_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchBusinessPlan_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var request = new PatchBusinessPlanRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/business-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- PatchSamplingPlan ---

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchSamplingPlan_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var request = new PatchSamplingPlanRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/sampling-plan",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // --- PatchOverseasSites ---

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchOverseasSites_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var request = new PatchOverseasSitesRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
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

    // RA-470 gap 6: the completeness gate above only ever checked Prns/BusinessPlan/SamplingPlan.
    // OverseasSites/BesEvidence are exporter-only sections (both null for a non-exporter, see
    // AccreditationApplicationModel), so the new checks are gated on IsExporter.
    [Fact]
    public async Task Submit_ExporterWithIncompleteOverseasSites_Returns400()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.IsExporter = true;
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    SectionStatus = SectionStatus.InProgress,
                };
                a.BesEvidence = new AccreditationApplicationBesEvidence
                {
                    SectionStatus = SectionStatus.Completed,
                };
            }
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

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_ExporterWithIncompleteBesEvidence_Returns400()
    {
        // RA-470 gap 5/6: this is what gives the endpoint's InProgress reset on a BES-evidence-
        // invalidating edit real teeth - without this gate the reset would only change a
        // task-list label, never actually block submission.
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.IsExporter = true;
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    SectionStatus = SectionStatus.Completed,
                };
                a.BesEvidence = new AccreditationApplicationBesEvidence
                {
                    SectionStatus = SectionStatus.InProgress,
                };
            }
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

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_ExporterWithAllSectionsComplete_Returns200()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Started,
            configure: a =>
            {
                a.IsExporter = true;
                a.Prns.SectionStatus = SectionStatus.Completed;
                a.BusinessPlan.SectionStatus = SectionStatus.Completed;
                a.SamplingPlan.SectionStatus = SectionStatus.Completed;
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    SectionStatus = SectionStatus.Completed,
                };
                a.BesEvidence = new AccreditationApplicationBesEvidence
                {
                    SectionStatus = SectionStatus.Completed,
                };
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
    }

    [Fact]
    public async Task Submit_NonExporterWithNullOverseasSitesAndBesEvidence_Unaffected()
    {
        // IsExporter = false (SeedApplication's default) leaves OverseasSites/BesEvidence null -
        // the new gap-6 checks must not block a non-exporter on sections it never has.
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

        var fileUploadId = await SeedValidatedUpload(
            "file-001",
            "plan.pdf",
            "sampling-plans/file-001"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task AddFile_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var fileUploadId = await SeedValidatedUpload(
            "file-004",
            "plan.pdf",
            "sampling-plans/file-004"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task DeleteFile_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddFile_InvalidFilename_Returns422()
    {
        Reset();
        var app = SeedApplication();

        var fileUploadId = await SeedValidatedUpload(
            "file-002",
            "../../etc/passwd",
            "sampling-plans/file-002"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task AddFile_ForbiddenContentType_Returns422()
    {
        Reset();
        var app = SeedApplication();

        var fileUploadId = await SeedValidatedUpload(
            "file-003",
            "script.js",
            "sampling-plans/file-003",
            contentType: "text/javascript"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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

        var fileUploadId = await SeedValidatedUpload(
            "file-new",
            "new.pdf",
            "sampling-plans/file-new"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
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

        var fileUploadId = await SeedValidatedUpload(
            "file-005",
            "plan.pdf",
            "sampling-plans/file-005"
        );
        var request = new FileUploadRequest { FileUploadId = fileUploadId };
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

        var fileUploadId = await SeedValidatedUpload(
            "file-008",
            "plan.pdf",
            "sampling-plans/file-008"
        );
        var request = new { FileUploadId = fileUploadId, DocumentType = 99 };
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

        var fileUploadId = await SeedValidatedUpload(
            "file-006",
            "plan.pdf",
            "sampling-plans/file-006"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
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

        var fileUploadId = await SeedValidatedUpload(
            "file-007",
            "evidence.pdf",
            "sampling-plans/file-007"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SupportingEvidence,
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

    [Fact]
    public async Task AddFile_WhenQueriedAndSamplingPlanSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var fileUploadId = await SeedValidatedUpload(
            "file-009",
            "plan.pdf",
            "sampling-plans/file-009"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
        };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddFile_WhenSamplingPlanSectionQueried_LeavesSectionStatusQueried()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.SamplingPlan.SectionStatus = SectionStatus.Queried
        );

        var fileUploadId = await SeedValidatedUpload(
            "file-010",
            "plan.pdf",
            "sampling-plans/file-010"
        );
        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
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
        stored!.SamplingPlan.SectionStatus.Should().Be(SectionStatus.Queried);
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

    [Fact]
    public async Task DeleteFile_WhenSaved_BumpsApplicationStatusToStarted()
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
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.ApplicationStatus.Should().Be(ApplicationStatus.Started);
    }

    [Fact]
    public async Task DeleteFile_WhenQueriedAndSamplingPlanSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Queried;
                a.SamplingPlan.Files.Add(
                    new AccreditationApplicationFile
                    {
                        FileId = "file-001",
                        Filename = "plan.pdf",
                        ContentType = "application/pdf",
                        UploadedByUserId = string.Empty,
                        S3Key = "sampling-plans/file-001",
                    }
                );
            }
        );

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteFile_WhenSamplingPlanSectionQueried_LeavesSectionStatusQueried()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.SamplingPlan.SectionStatus = SectionStatus.Queried;
                a.SamplingPlan.Files.Add(
                    new AccreditationApplicationFile
                    {
                        FileId = "file-001",
                        Filename = "plan.pdf",
                        ContentType = "application/pdf",
                        UploadedByUserId = string.Empty,
                        S3Key = "sampling-plans/file-001",
                    }
                );
            }
        );

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.SamplingPlan.SectionStatus.Should().Be(SectionStatus.Queried);
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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task AddOverseasSite_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddOverseasSite_WhenQueriedAndOverseasSitesSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a => a.Prns.SectionStatus = SectionStatus.Queried
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddOverseasSite_WhenOverseasSitesSectionQueried_LeavesSectionStatusQueried()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    SectionStatus = SectionStatus.Queried,
                }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.OverseasSites!.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    [Fact]
    public async Task AddOverseasSite_FirstSiteOnApplication_SectionStatusIsCompleted()
    {
        // OverseasSites has no InProgress concept: a selected site means the section is done,
        // matching AccreditationApplicationSections.ComputeCurrentStatus. So the very first site
        // added completes the section immediately rather than passing through InProgress.
        Reset();
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        stored!.OverseasSites!.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public async Task AddOverseasSite_MissingSiteName_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = new AddOverseasSiteRequest
        {
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

    [Theory]
    [InlineData("Y46")]
    [InlineData("Y47")]
    [InlineData("Y48")]
    [InlineData("Y49")]
    [InlineData("y46")]
    public async Task AddOverseasSite_ApprovedYCode_Returns201(string code)
    {
        // Y46-Y49 are on the approved Basel/OECD list but match neither shape the old
        // BaselOecdRegex accepted - this is the bug the membership check fixes.
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { Code1 = code };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddOverseasSite_ShapeValidButUnlistedBaselCode_Returns400()
    {
        // "Z9999" matches the old shape regex (letter + 4 digits) but is not on the
        // approved list, so it must now be rejected by the membership check.
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { Code1 = "Z9999" };

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddOverseasSite_DuplicateCode_Returns400()
    {
        Reset();
        var app = SeedApplication();

        var request = ValidAddOrsRequest() with { Code1 = "A1181", Code2 = "A1181" };

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

    // RA-482: OrsId is server-generated now, so the client can no longer supply a colliding
    // value -- this supersedes the old "client sends a duplicate OrsId" 409 test. The
    // equivalent case today is that generation skips past an existing id rather than reusing it.
    [Fact]
    public async Task AddOverseasSite_ExistingSiteHasOrsId001_NextSiteGetsOrsId002()
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

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.OrsId.Should().Be("002");
    }

    // RA-482: OrsId uniqueness spans every application under the same RegistrationId, not just
    // the current one -- a prior year's application (a separate AccreditationApplicationModel,
    // same RegistrationId) already holds "005", so the new site on THIS year's application must
    // continue from there rather than restarting at "001".
    [Fact]
    public async Task AddOverseasSite_PriorYearApplicationSharesRegistrationId_ContinuesItsSequence()
    {
        Reset();
        SeedApplication(configure: a =>
        {
            a.RegistrationId = "reg-1";
            a.Year = 2025;
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "Prior Year",
                        OrsId = "005",
                    },
                ],
            };
        });
        var thisYearApp = SeedApplication(configure: a =>
        {
            a.RegistrationId = "reg-1";
            a.Year = 2026;
        });

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{thisYearApp.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.OrsId.Should().Be("006");
    }

    // A different registration must not see another registration's ids, even for the same org.
    [Fact]
    public async Task AddOverseasSite_DifferentRegistrationIdSameOrg_HasIndependentSequence()
    {
        Reset();
        SeedApplication(configure: a =>
        {
            a.RegistrationId = "reg-other";
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "Other Reg",
                        OrsId = "009",
                    },
                ],
            };
        });
        var app = SeedApplication(configure: a => a.RegistrationId = "reg-1");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.OrsId.Should().Be("001");
    }

    // A deselected site was never removed from the persisted list (only flagged), so it must
    // still count toward the max -- otherwise a re-add could reissue its id.
    [Fact]
    public async Task AddOverseasSite_ExistingSiteIsDeselected_StillCountsTowardMax()
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
                        SiteName = "Deselected",
                        OrsId = "003",
                        Selected = false,
                    },
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
        site!.OrsId.Should().Be("004");
    }

    // RA-482 capacity guard: the 3-digit format caps at "999" -- generating past it must fail
    // loudly rather than silently emit a 4-digit value that breaks the format contract.
    [Fact]
    public async Task AddOverseasSite_ExistingMaxIs999_Returns422CapacityError()
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
                        SiteName = "At Capacity",
                        OrsId = "999",
                    },
                ],
            }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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

    [Theory]
    [InlineData("Y46")]
    [InlineData("Y47")]
    [InlineData("Y48")]
    [InlineData("Y49")]
    public async Task PromoteOverseasSite_ApprovedYCode_Returns200(string code)
    {
        // Y46-Y49 are on the approved Basel/OECD list but match neither shape the old
        // BaselOecdRegex accepted - this is the bug the membership check fixes.
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidPromoteRequest() with { Code1 = code };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PromoteOverseasSite_ShapeValidButUnlistedBaselCode_Returns400()
    {
        // "Z9999" matches the old shape regex (letter + 4 digits) but is not on the
        // approved list, so it must now be rejected by the membership check.
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidPromoteRequest() with { Code1 = "Z9999" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PromoteOverseasSite_DuplicateCode_Returns400()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidPromoteRequest() with { Code1 = "A1181", Code2 = "A1181" };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PromoteOverseasSite_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(
            status: status,
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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task RevertOverseasSite_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(
            status: status,
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

    // --- UpdateOverseasSite (RA-470) ---

    private static string UpdateOverseasSiteUrl(AccreditationApplicationModel app, int siteId) =>
        $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/{siteId}";

    // Reuses PromoteOverseasSiteRequest/its validator exactly, per RA-470's contract with the
    // frontend - OperationCodes is deliberately ["R4"], not Promote's own ["R3"]: the default
    // SeedApplication MaterialType is Steel, whose applicable codes are R4/R12/R13
    // (RecyclingOperationCodes.CodesByMaterialType) - R3 would fail UpdateOverseasSite's gap-2
    // material-type check even though it's fine for Promote (Promote doesn't run that check).
    private static PromoteOverseasSiteRequest ValidUpdateOverseasSiteRequest() =>
        new()
        {
            SiteName = "Updated Recycling GmbH",
            AddressLine1 = "Neue Strasse 2",
            TownOrCity = "Munich",
            Country = "Germany",
            ContactName = "Greta Schmidt",
            ContactEmail = "greta@updatedrecycling.de",
            OperationCodes = ["R4"],
            Code1 = "A1181",
            RepatriatedLoads = "Rejected loads returned within 30 days at our expense.",
        };

    private static InterimSiteModel TestInterimSite(int siteId = 2) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = "SN-0002",
            Country = "France",
            SiteName = "Interim Site",
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Jane Smith",
            ContactEmail = "jane.smith@example.com",
            ContactPhone = "+33 1 23 45 67 89",
        };

    [Fact]
    public async Task UpdateOverseasSite_ValidRequest_UpdatesFieldsWithoutSettingSelectedOrRegisteredNowAccredited()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            ValidUpdateOverseasSiteRequest(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        site!.SiteId.Should().Be(900001);
        site.SiteName.Should().Be("Updated Recycling GmbH");
        site.Country.Should().Be("Germany");
        // RegisteredOnlySite() seeds both false - UpdateOverseasSite must leave them exactly as
        // found, unlike PromoteOverseasSite which forces both true.
        site.Selected.Should().BeFalse();
        site.RegisteredNowAccredited.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateOverseasSite_MissingSiteName_Returns400()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidUpdateOverseasSiteRequest() with { SiteName = "" };
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            request,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateOverseasSite_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.PatchAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/nonexistent-id/overseas-sites/900001",
            ValidUpdateOverseasSiteRequest(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateOverseasSite_SiteNotFound_Returns404()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [] }
        );

        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            ValidUpdateOverseasSiteRequest(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task UpdateOverseasSite_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(
            status: status,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [RegisteredOnlySite()],
                }
        );

        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            ValidUpdateOverseasSiteRequest(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateOverseasSite_WhenQueriedAndOverseasSitesSectionNotQueried_Returns409()
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

        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            ValidUpdateOverseasSiteRequest(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // Gap 2: shared ValidateOperationCodesForSite catches material-type/interim-site issues that
    // PromoteOverseasSiteRequestValidator alone never checked.
    [Fact]
    public async Task UpdateOverseasSite_CodeNotApplicableToMaterialType_Returns400()
    {
        // Default SeedApplication MaterialType is Steel - applicable codes are R4/R12/R13
        // (RecyclingOperationCodes.CodesByMaterialType) - R3 is a valid code in general but not
        // offered for Steel.
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidUpdateOverseasSiteRequest() with { OperationCodes = ["R3"] };
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            request,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Mirrors RecyclingOperationsEndpointTests'
    // PatchRecyclingOperations_MaterialTypeApplicability_MatchesFrontendCodesByMaterialType for
    // this endpoint - every prior UpdateOverseasSite test above uses the default SeedApplication
    // MaterialType (Steel) exclusively, so none of them could catch a drift between
    // RecyclingOperationCodes.CodesByMaterialType and the frontend's own CODES_BY_MATERIAL_TYPE
    // for any other material - which is exactly the shape of the story's own reported Plastic-
    // material Change-journey failure. Neither allowedCode nor disallowedCode is R12/R13 for any
    // material in this table, so this doesn't exercise the interim-site sub-check above.
    [Theory]
    [InlineData(MaterialType.Aluminium, "R4", "R3")]
    [InlineData(MaterialType.Fibre, "R3", "R4")]
    [InlineData(MaterialType.Glass, "R5", "R3")]
    [InlineData(MaterialType.Paper, "R3", "R4")]
    [InlineData(MaterialType.Plastic, "R3", "R4")]
    [InlineData(MaterialType.Steel, "R4", "R3")]
    [InlineData(MaterialType.Wood, "R3", "R4")]
    public async Task UpdateOverseasSite_MaterialTypeApplicability_MatchesFrontendCodesByMaterialType(
        MaterialType materialType,
        string allowedCode,
        string disallowedCode
    )
    {
        Reset();
        var allowedApp = SeedApplication(configure: a =>
        {
            a.MaterialType = materialType;
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            };
        });

        var allowedResponse = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(allowedApp, 900001),
            ValidUpdateOverseasSiteRequest() with
            {
                OperationCodes = [allowedCode],
            },
            TestContext.Current.CancellationToken
        );

        allowedResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, $"{allowedCode} should be applicable for {materialType}");

        var disallowedApp = SeedApplication(configure: a =>
        {
            a.MaterialType = materialType;
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            };
        });

        var disallowedResponse = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(disallowedApp, 900001),
            ValidUpdateOverseasSiteRequest() with
            {
                OperationCodes = [disallowedCode],
            },
            TestContext.Current.CancellationToken
        );

        disallowedResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.BadRequest,
                $"{disallowedCode} should not be applicable for {materialType}"
            );
    }

    [Fact]
    public async Task UpdateOverseasSite_R12WithNoInterimSite_Returns200()
    {
        // RA-486: R12/R13 no longer imply an interim site must exist - the old AC11 "R12/R13
        // needs an existing interim site" sub-check has been removed entirely, not just skipped
        // for this endpoint.
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );

        var request = ValidUpdateOverseasSiteRequest() with { OperationCodes = ["R4", "R12"] };
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            request,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // RA-486: the ORS's OperationCodes and any attached interim site are independent now -
    // dropping R12/R13 from the ORS no longer disturbs an existing interim site (whose own
    // mandatory-code rule lives on InterimSiteModel.OperationCodes instead). Supersedes the old
    // Gap 1 "an interim site can't be left dangling with no R12/R13 justifying it" 400.
    [Fact]
    public async Task UpdateOverseasSite_InterimSitePresent_DroppingR12R13_Returns200()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 900001,
                        SiteName = "Site With Interim",
                        Country = "France",
                        OperationCodes = ["R4", "R12"],
                        InterimSite = TestInterimSite(),
                    },
                ],
            }
        );

        var request = ValidUpdateOverseasSiteRequest() with { OperationCodes = ["R4"] };
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            request,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateOverseasSite_InterimSitePresent_KeepingR12_Returns200()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 900001,
                        SiteName = "Site With Interim",
                        Country = "France",
                        OperationCodes = ["R4", "R12"],
                        InterimSite = TestInterimSite(),
                    },
                ],
            }
        );

        var request = ValidUpdateOverseasSiteRequest() with { OperationCodes = ["R4", "R12"] };
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            request,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Gap 3: audit operator-driven operation-code changes the same way PatchRecyclingOperations
    // audits regulator-driven ones.
    [Fact]
    public async Task UpdateOverseasSite_OperationCodesChanged_WritesAuditRecordWithBeforeAndAfterCodes()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 900001,
                        SiteName = "Site With Interim",
                        Country = "France",
                        OperationCodes = ["R4"],
                        InterimSite = TestInterimSite(),
                    },
                ],
            }
        );

        var request = ValidUpdateOverseasSiteRequest() with { OperationCodes = ["R4", "R12"] };
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            request,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockAuditPersistence.Received(1)
            .RecordAsync(
                Arg.Is<RecyclingOperationsAuditRecord>(r =>
                    r.OrganisationId == app.OrganisationId
                    && r.ApplicationId == app.ApplicationId
                    && r.SiteId == 900001
                    && r.BeforeCodes.SequenceEqual(new[] { "R4" })
                    && r.AfterCodes.SequenceEqual(new[] { "R4", "R12" })
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task UpdateOverseasSite_OperationCodesUnchanged_DoesNotWriteAuditRecord()
    {
        // Unlike PatchRecyclingOperations (which only ever changes codes), UpdateOverseasSite
        // edits many other fields too - a record with identical BeforeCodes/AfterCodes on every
        // address/contact-only edit would dilute an audit trail meant specifically for
        // operation-code changes, so it's written only when the codes actually changed.
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 900001,
                        SiteName = "Registered Only Site",
                        Country = "France",
                        OperationCodes = ["R4"],
                    },
                ],
            }
        );

        var request = ValidUpdateOverseasSiteRequest() with { OperationCodes = ["R4"] };
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            request,
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockAuditPersistence.DidNotReceive()
            .RecordAsync(Arg.Any<RecyclingOperationsAuditRecord>(), Arg.Any<CancellationToken>());
    }

    // Gap 5: BES evidence is tied to Country/ConditionsOfExport - either changing makes any
    // previously uploaded evidence, and a Completed section status, stale.
    [Fact]
    public async Task UpdateOverseasSite_CountryChanged_ClearsBesEvidenceUploadsAndResetsCompletedStatus()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 900001,
                        SiteName = "Registered Only Site",
                        Country = "France",
                        OperationCodes = ["R4"],
                        ConditionsOfExport = false,
                        BesEvidence = new BesEvidenceModel
                        {
                            BesEvidenceUploads =
                            [
                                new BesEvidenceFileModel
                                {
                                    FileId = "file-1",
                                    Filename = "evidence.pdf",
                                    S3Key = "bes-evidence/file-1",
                                },
                            ],
                        },
                    },
                ],
            };
            a.BesEvidence = new AccreditationApplicationBesEvidence
            {
                SectionStatus = SectionStatus.Completed,
            };
        });

        // ValidUpdateOverseasSiteRequest's Country is "Germany" - differs from the seeded "France".
        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            ValidUpdateOverseasSiteRequest(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            app.OrganisationId,
            app.Id!.Value.ToString()
        );
        var storedSite = stored!.OverseasSites!.Sites.Single(s => s.SiteId == 900001);
        storedSite.BesEvidence!.BesEvidenceUploads.Should().BeEmpty();
        stored.BesEvidence!.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public async Task UpdateOverseasSite_NoCountryOrConditionsChange_LeavesBesEvidenceUntouched()
    {
        Reset();
        var app = SeedApplication(configure: a =>
        {
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 900001,
                        SiteName = "Registered Only Site",
                        // Matches ValidUpdateOverseasSiteRequest's Country ("Germany") and
                        // ConditionsOfExport (unset/null) exactly, so neither changes.
                        Country = "Germany",
                        ConditionsOfExport = null,
                        OperationCodes = ["R4"],
                        BesEvidence = new BesEvidenceModel
                        {
                            BesEvidenceUploads =
                            [
                                new BesEvidenceFileModel
                                {
                                    FileId = "file-1",
                                    Filename = "evidence.pdf",
                                    S3Key = "bes-evidence/file-1",
                                },
                            ],
                        },
                    },
                ],
            };
            a.BesEvidence = new AccreditationApplicationBesEvidence
            {
                SectionStatus = SectionStatus.Completed,
            };
        });

        var response = await _client.PatchAsJsonAsync(
            UpdateOverseasSiteUrl(app, 900001),
            ValidUpdateOverseasSiteRequest(),
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _factory.FakePersistence.GetByIdAsync(
            app.OrganisationId,
            app.Id!.Value.ToString()
        );
        var storedSite = stored!.OverseasSites!.Sites.Single(s => s.SiteId == 900001);
        storedSite.BesEvidence!.BesEvidenceUploads.Should().ContainSingle();
        stored.BesEvidence!.SectionStatus.Should().Be(SectionStatus.Completed);
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
            OperationCodes = ["R12"],
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
        interimSite.OperationCodes.Should().BeEquivalentTo(["R12"]);
    }

    // RA-486: interim site OperationCodes must include at least one of R12/R13 - R3/R4/R5 are
    // optional on the interim site (material type is inherited from the parent ORS).
    [Theory]
    [InlineData(new string[] { }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R3" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R99" }, HttpStatusCode.BadRequest)]
    [InlineData(new[] { "R12" }, HttpStatusCode.Created)]
    [InlineData(new[] { "R13" }, HttpStatusCode.Created)]
    [InlineData(new[] { "R3", "R12" }, HttpStatusCode.Created)]
    public async Task AddInterimSite_OperationCodesMandatoryRule_MatchesAc(
        string[] operationCodes,
        HttpStatusCode expectedStatus
    )
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = ValidAddInterimSiteRequest() with { OperationCodes = [.. operationCodes] };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(expectedStatus);
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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task AddInterimSite_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(
            status: status,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Test Site" }],
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
    public async Task AddInterimSite_WhenQueriedAndOverseasSitesSectionNotQueried_Returns409()
    {
        Reset();
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
            {
                a.Prns.SectionStatus = SectionStatus.Queried;
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Test Site" }],
                };
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

        var fileUploadId = await SeedValidatedUpload(
            "bes-file-001",
            "evidence.pdf",
            "bes-evidence/bes-file-001"
        );
        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddBesEvidenceFile_EmptyFileUploadId_Returns400()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = new AddBesEvidenceFileRequest { FileUploadId = string.Empty };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddBesEvidenceFile_InvalidFilename_Returns422()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var fileUploadId = await SeedValidatedUpload(
            "bes-file-003",
            "../../etc/passwd",
            "bes-evidence/bes-file-003"
        );
        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
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
        // Updated = a prior query was already resolved via resubmit; the Case Management service
        // must be able to raise a fresh query against the same application.
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

        var fileUploadId = await SeedValidatedUpload(
            "bes-file-001",
            "evidence.pdf",
            "bes-evidence/bes-file-001"
        );
        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task AddBesEvidenceFile_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(
            status: status,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Test Site" }],
                }
        );

        var fileUploadId = await SeedValidatedUpload(
            "bes-file-002",
            "evidence.pdf",
            "bes-evidence/bes-file-002"
        );
        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task InitiateUpload_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchBesEvidence_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task DeleteBesEvidenceFile_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task InitiateBesEvidenceUpload_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

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

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchBesEvidenceSection_WhenTerminal_Returns409(ApplicationStatus status)
    {
        Reset();
        var app = SeedApplication(status: status);

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
        // Mirrors the Case Management service's continue-review-during-duly-making action, which
        // can push the Case Management service (and therefore the Registration & Accreditation
        // service) back to 'submitted' from 'updated' — ordering is timestamp-based, not
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
    [InlineData("assessment-in-progress", ApplicationStatus.Updated)]
    [InlineData("awaiting-decision", ApplicationStatus.AwaitingDecision)]
    public async Task StatusChangedFromCaseManagement_MapsPaymentReceivedAndSubmitForDecisionStates(
        string toStateId,
        ApplicationStatus expectedStatus
    )
    {
        // RA-368: these two states used to have no mapping arm and were silently dropped,
        // leaving the Registration & Accreditation service pinned at 'DulyMade' while the Case
        // Management service had already moved on.
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
        body!.ApplicationStatus.Should().Be(expectedStatus);
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

    // --- RA-368: AwaitingDecision legality gates (approve/reject, query, withdraw) ---

    [Fact]
    public async Task StatusChangedFromCaseManagement_ApprovedFromAwaitingDecision_Succeeds()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.AwaitingDecision,
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
    public async Task QueryFromCaseManagement_AwaitingDecisionStatus_Succeeds()
    {
        Reset();
        var workItemId = Guid.NewGuid();
        SeedApplication(
            status: ApplicationStatus.AwaitingDecision,
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
    public async Task Withdraw_FromAwaitingDecision_Succeeds()
    {
        Reset();
        var app = SeedApplication(status: ApplicationStatus.AwaitingDecision);
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
