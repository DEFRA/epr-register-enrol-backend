using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// Targeted branch-coverage tests for the overseas-site, interim-site, file and CDP-upload
// endpoint handlers in AccreditationApplicationEndpoints. Kept in a separate file (rather than
// appended to the large existing AccreditationApplicationEndpointsTests) to avoid merge
// conflicts with other work happening on that file in parallel. Fixture helpers below are
// deliberately copied from that file's private Reset()/SeedApplication() rather than shared,
// for the same reason.
public class AccreditationApplicationEndpointsSitesFilesTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsSitesFilesTests(
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

    // Simulates a real CDP-uploader webhook callback having already completed for
    // fileUploadId, so AddFile can resolve it via the real IPendingUploadService singleton
    // instead of trusting client-supplied file fields.
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

    // ================= AddOverseasSite =================

    [Fact]
    public async Task AddOverseasSite_AtMaxSiteCount_Returns422()
    {
        Reset();
        var sites = Enumerable
            .Range(1, 500)
            .Select(i => new OverseasSiteModel { SiteId = i, SiteName = $"Site {i}" })
            .ToList();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = sites }
        );

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // RA-482: OrsId generation moved server-side and now writes via UpdateIfOrsIdAbsentAsync, so
    // a persistence write failure surfaces through the retry-on-conflict loop (bounded at 3
    // attempts) rather than a single-attempt UpdateAsync -- exhausting it is a 409 Conflict, not
    // a 500. Supersedes the old single-failure "ReturnsProblem" test for this endpoint.
    [Fact]
    public async Task AddOverseasSite_WhenOrsIdWriteKeepsConflicting_ReturnsConflictAfterRetries()
    {
        Reset();
        var app = SeedApplication();
        _factory.FakePersistence.FailNextOrsIdWrites = 3;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites",
            ValidAddOrsRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddOverseasSite_WhenOrsIdWriteConflictsOnce_RetriesAndSucceeds()
    {
        Reset();
        var app = SeedApplication();
        _factory.FakePersistence.FailNextOrsIdWrites = 1;

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
        site!.OrsId.Should().Be("001");
    }

    [Fact]
    public async Task AddOverseasSite_WhenNotifyThrows_StillReturns201()
    {
        Reset();
        var app = SeedApplication(configure: a => a.CaseManagementWorkItemId = Guid.NewGuid());
        _factory
            .MockCaseWorkingAdapter.NotifySiteAddedAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromException(new HttpRequestException("management-be unreachable")));

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
        site!.OrsId.Should().Be("001");
    }

    // ================= PromoteOverseasSite =================

    [Fact]
    public async Task PromoteOverseasSite_WhenPersistenceUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [RegisteredOnlySite()],
            }
        );
        _factory.FakePersistence.FailNextUpdate = true;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task PromoteOverseasSite_WhenNoOverseasSitesAtAllOnApplication_Returns404()
    {
        Reset();
        // OverseasSites is left entirely null (not just an empty list), exercising the
        // `application.OverseasSites?.Sites.FirstOrDefault(...)` null-conditional's null side.
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/promote",
            ValidPromoteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ================= RevertOverseasSite =================

    [Fact]
    public async Task RevertOverseasSite_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.PostAsync(
            "/api/v1/accreditation-applications/org-123/nonexistent-id/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RevertOverseasSite_WhenQueriedAndOverseasSitesSectionNotQueried_Returns409()
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

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RevertOverseasSite_WhenPersistenceUpdateFails_ReturnsProblem()
    {
        Reset();
        var site = RegisteredOnlySite();
        site.RegisteredNowAccredited = true;
        site.PreviousSites.Add(RegisteredOnlySite());
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [site] }
        );
        _factory.FakePersistence.FailNextUpdate = true;

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task RevertOverseasSite_RegisteredButNoPreviousSites_Returns409()
    {
        Reset();
        // RegisteredNowAccredited is true (so the `!site.RegisteredNowAccredited` side of the OR
        // guard is false) but PreviousSites is empty, exercising the OR's second operand.
        var site = RegisteredOnlySite();
        site.RegisteredNowAccredited = true;
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [site] }
        );

        var response = await _client.PostAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/900001/revert",
            content: null,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ================= AddInterimSite =================

    private AccreditationApplicationModel SeedApplicationWithOverseasSite(
        int siteId = 1,
        string siteName = "Test Site",
        string? orsId = null
    ) =>
        SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = siteId,
                        SiteName = siteName,
                        OrsId = orsId,
                    },
                ],
            }
        );

    [Fact]
    public async Task AddInterimSite_WhenPersistenceUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        _factory.FakePersistence.FailNextUpdate = true;

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public async Task AddInterimSite_WithLinkedWorkItemAndNullOrsId_NotifiesWithEmptyOrsId()
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
                        OrsId = null,
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
                string.Empty,
                "SN-0002",
                true,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task AddInterimSite_WhenApplicationHasNoOverseasSitesAtAll_Returns404()
    {
        Reset();
        // OverseasSites itself is null (never populated), rather than an empty/mismatched list,
        // exercising the null side of `application.OverseasSites?.Sites.FirstOrDefault(...)`.
        var app = SeedApplication();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/interim-site",
            ValidAddInterimSiteRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ================= AddFile =================

    [Fact]
    public async Task AddFile_ApplicationNotFound_Returns404()
    {
        Reset();
        var fileUploadId = await SeedValidatedUpload(
            "file-missing-app",
            "plan.pdf",
            "sampling-plans/file-missing-app"
        );

        var request = new FileUploadRequest
        {
            FileUploadId = fileUploadId,
            DocumentType = AccreditationFileDocumentType.SamplingPlan,
        };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/accreditation-applications/org-123/nonexistent-id/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddFile_WhenPersistenceUpdateFails_ReturnsProblem()
    {
        Reset();
        var app = SeedApplication();
        var fileUploadId = await SeedValidatedUpload(
            "file-update-fails",
            "plan.pdf",
            "sampling-plans/file-update-fails"
        );
        _factory.FakePersistence.FailNextUpdate = true;

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

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // ================= DeleteFile =================

    [Fact]
    public async Task DeleteFile_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.DeleteAsync(
            "/api/v1/accreditation-applications/org-123/nonexistent-id/files/file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteFile_FileNotFound_Returns404()
    {
        Reset();
        var app = SeedApplication();

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/nonexistent-file",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteFile_WhenPersistenceUpdateFails_ReturnsProblem()
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
        _factory.FakePersistence.FailNextUpdate = true;

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/files/file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // ================= InitiateUpload / InitiateUploadInternal =================

    [Fact]
    public async Task InitiateUpload_WithClientSuppliedMetadata_MergesFileUploadIdIntoIt()
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
            metadata = new Dictionary<string, string> { ["source"] = "operator-portal" },
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
                    r.Metadata != null
                    && r.Metadata.ContainsKey("source")
                    && r.Metadata["source"] == "operator-portal"
                    && r.Metadata.ContainsKey("fileUploadId")
                ),
                Arg.Any<CancellationToken>()
            );
    }
}
