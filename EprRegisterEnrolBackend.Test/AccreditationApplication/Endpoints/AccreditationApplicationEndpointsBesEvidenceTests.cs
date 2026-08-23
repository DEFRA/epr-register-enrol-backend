using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// Additional branch-coverage tests for the four BES evidence endpoints
// (AddBesEvidenceFile, PatchBesEvidence, DeleteBesEvidenceFile, PatchBesEvidenceSection).
// Kept in a separate file from AccreditationApplicationEndpointsTests to avoid merge conflicts
// with other in-flight work on that file; existing coverage there (409/400 gate-wiring tests,
// the happy-path 201 for AddBesEvidenceFile, and validator-rejection tests) is not duplicated.
public class AccreditationApplicationEndpointsBesEvidenceTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsBesEvidenceTests(
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
    // fileUploadId, so AddBesEvidenceFile can resolve it via the real IPendingUploadService
    // singleton instead of trusting client-supplied file fields.
    private string SeedValidatedUpload(
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
        pendingUploadService.Create(fileUploadId, "https://cdp.example/status");
        pendingUploadService.Complete(
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

    // Builds a standalone, pre-seeded IPendingUploadService for use with
    // CreateClientWithFailingUpdate, whose swapped-in HttpClient runs against a separate
    // WebApplicationFactory host (and therefore a separate IPendingUploadService singleton
    // instance) from _factory/_client.
    private static (string FileUploadId, IPendingUploadService Service) CreateSeededPendingUploadService(
        string fileId,
        string filename,
        string s3Key
    )
    {
        var fileUploadId = $"upload-{fileId}";
        var service = new PendingUploadService(EnabledNullLogger<PendingUploadService>.Instance);
        service.Create(fileUploadId, "https://cdp.example/status");
        service.Complete(
            fileUploadId,
            new CdpCallbackFile
            {
                FileId = fileId,
                Filename = filename,
                FileStatus = "complete",
                ContentType = "application/pdf",
                S3Key = s3Key,
                S3Bucket = "test-bucket",
            }
        );
        return (fileUploadId, service);
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

    private AccreditationApplicationModel SeedApplicationWithOverseasSite(
        int siteId = 1,
        string siteName = "Test Site",
        ApplicationStatus status = ApplicationStatus.Saved,
        Action<AccreditationApplicationModel>? configure = null
    ) =>
        SeedApplication(
            status: status,
            configure: a =>
            {
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [new OverseasSiteModel { SiteId = siteId, SiteName = siteName }],
                };
                configure?.Invoke(a);
            }
        );

    // Creates an isolated HttpClient whose IAccreditationApplicationPersistence is swapped for a
    // substitute that returns the given application from GetByIdAsync but null from UpdateAsync,
    // to exercise the "updated is null -> Results.Problem" branch that FakeAccreditationApplicationPersistence
    // cannot otherwise produce (its UpdateAsync only returns null for an application that was never
    // seeded, in which case GetByIdAsync would also have returned null, producing a 404 instead).
    private HttpClient CreateClientWithFailingUpdate(
        AccreditationApplicationModel application,
        IPendingUploadService? pendingUploadService = null
    )
    {
        var brokenPersistence = Substitute.For<IAccreditationApplicationPersistence>();
        brokenPersistence
            .GetByIdAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult<AccreditationApplicationModel?>(application));
        brokenPersistence
            .UpdateAsync(Arg.Any<AccreditationApplicationModel>())
            .Returns(Task.FromResult<AccreditationApplicationModel?>(null));

        var factoryWithBrokenPersistence = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(brokenPersistence);
                if (pendingUploadService is not null)
                    services.AddSingleton(pendingUploadService);
            })
        );
        return factoryWithBrokenPersistence.CreateClient();
    }

    // --- AddBesEvidenceFile ---

    [Fact]
    public async Task AddBesEvidenceFile_ApplicationNotFound_Returns404()
    {
        Reset();
        var fileUploadId = SeedValidatedUpload(
            "bes-file-404",
            "evidence.pdf",
            "bes-evidence/bes-file-404"
        );

        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddBesEvidenceFile_SiteNotFound_Returns404()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(siteId: 1);
        var fileUploadId = SeedValidatedUpload(
            "bes-file-405",
            "evidence.pdf",
            "bes-evidence/bes-file-405"
        );

        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/999/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddBesEvidenceFile_SiteAlreadyHasBesEvidence_AppendsToExistingUploads()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        app.OverseasSites!.Sites[0].BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "existing-file",
                    Filename = "existing.pdf",
                    S3Key = "bes-evidence/existing-file",
                },
            ],
        };

        var fileUploadId = SeedValidatedUpload("bes-file-new", "new.pdf", "bes-evidence/bes-file-new");
        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<BesEvidenceModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.BesEvidenceUploads.Should().HaveCount(2);
        body.BesEvidenceUploads.Select(f => f.FileId)
            .Should()
            .BeEquivalentTo(["existing-file", "bes-file-new"]);
    }

    [Fact]
    public async Task AddBesEvidenceFile_WhenUpdateFails_Returns500()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        var (fileUploadId, pendingUploadService) = CreateSeededPendingUploadService(
            "bes-file-fail",
            "evidence.pdf",
            "bes-evidence/bes-file-fail"
        );
        using var client = CreateClientWithFailingUpdate(app, pendingUploadService);

        var request = new AddBesEvidenceFileRequest { FileUploadId = fileUploadId };
        var response = await client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // --- PatchBesEvidence ---

    [Fact]
    public async Task PatchBesEvidence_ApplicationNotFound_Returns404()
    {
        Reset();

        var request = new PatchBesEvidenceRequest { DoYouWantToUploadMoreEvidence = true };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/overseas-sites/1/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchBesEvidence_SiteNotFound_Returns404()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(siteId: 1);

        var request = new PatchBesEvidenceRequest { DoYouWantToUploadMoreEvidence = true };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/999/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchBesEvidence_WithValueSet_UpdatesFlagAndReturns200()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var request = new PatchBesEvidenceRequest { DoYouWantToUploadMoreEvidence = false };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OverseasSites!.Sites[0].BesEvidence!.DoYouWantToUploadMoreEvidence.Should().BeFalse();
    }

    [Fact]
    public async Task PatchBesEvidence_WithNoValueSet_LeavesExistingFlagUnchangedAndReturns200()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        app.OverseasSites!.Sites[0].BesEvidence = new BesEvidenceModel
        {
            DoYouWantToUploadMoreEvidence = true,
        };

        var request = new PatchBesEvidenceRequest();
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AccreditationApplicationModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OverseasSites!.Sites[0].BesEvidence!.DoYouWantToUploadMoreEvidence.Should().BeTrue();
    }

    [Fact]
    public async Task PatchBesEvidence_WhenUpdateFails_Returns500()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        using var client = CreateClientWithFailingUpdate(app);

        var request = new PatchBesEvidenceRequest { DoYouWantToUploadMoreEvidence = true };
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // --- DeleteBesEvidenceFile ---

    [Fact]
    public async Task DeleteBesEvidenceFile_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/overseas-sites/1/bes-evidence/files/bes-file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_SiteNotFound_Returns404()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(siteId: 1);

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/999/bes-evidence/files/bes-file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_SiteHasNoBesEvidence_Returns404()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        // site.BesEvidence left null

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files/bes-file-001",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_FileIdNotInUploads_Returns404()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        app.OverseasSites!.Sites[0].BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "other-file",
                    Filename = "other.pdf",
                    S3Key = "bes-evidence/other-file",
                },
            ],
        };

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files/does-not-exist",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_FileIdMatches_RemovesFileAndReturns200()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        app.OverseasSites!.Sites[0].BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "target-file",
                    Filename = "target.pdf",
                    S3Key = "bes-evidence/target-file",
                },
                new BesEvidenceFileModel
                {
                    FileId = "keep-file",
                    Filename = "keep.pdf",
                    S3Key = "bes-evidence/keep-file",
                },
            ],
        };

        var response = await _client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files/target-file",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = await _factory.FakePersistence.GetByIdAsync(
            "org-123",
            app.Id!.Value.ToString()
        );
        var uploads = stored!.OverseasSites!.Sites[0].BesEvidence!.BesEvidenceUploads;
        uploads.Should().HaveCount(1);
        uploads[0].FileId.Should().Be("keep-file");
    }

    [Fact]
    public async Task DeleteBesEvidenceFile_WhenUpdateFails_Returns500()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        app.OverseasSites!.Sites[0].BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel
                {
                    FileId = "target-file",
                    Filename = "target.pdf",
                    S3Key = "bes-evidence/target-file",
                },
            ],
        };
        using var client = CreateClientWithFailingUpdate(app);

        var response = await client.DeleteAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/overseas-sites/1/bes-evidence/files/target-file",
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // --- PatchBesEvidenceSection ---

    [Fact]
    public async Task PatchBesEvidenceSection_ApplicationNotFound_Returns404()
    {
        Reset();

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_ApplicationHasNoBesEvidenceYet_CreatesItAndSetsStatus()
    {
        Reset();
        var app = SeedApplication();
        // application.BesEvidence left null so ??= new AccreditationApplicationBesEvidence() runs

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.InProgress,
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
        body!.BesEvidence!.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_NoSectionStatusProvided_LeavesStatusUnchanged()
    {
        Reset();
        var app = SeedApplication(
            configure: a =>
                a.BesEvidence = new AccreditationApplicationBesEvidence
                {
                    SectionStatus = SectionStatus.InProgress,
                }
        );

        var request = new PatchBesEvidenceSectionRequest { SectionStatus = null };
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
        body!.BesEvidence!.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_WhenAlreadyQueried_GuardPreventsOverwriteAndStaysQueried()
    {
        Reset();
        // ApplicationStatus == Queried only passes IsSectionEditable when the BesEvidence section
        // itself is already Queried, so this is the only way to reach the inner
        // "SectionStatus != Queried" guard with it evaluating false.
        var app = SeedApplication(
            status: ApplicationStatus.Queried,
            configure: a =>
                a.BesEvidence = new AccreditationApplicationBesEvidence
                {
                    SectionStatus = SectionStatus.Queried,
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
        body!.BesEvidence!.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    [Fact]
    public async Task PatchBesEvidenceSection_WhenUpdateFails_Returns500()
    {
        Reset();
        var app = SeedApplication();
        using var client = CreateClientWithFailingUpdate(app);

        var request = new PatchBesEvidenceSectionRequest
        {
            SectionStatus = SectionStatus.Completed,
        };
        var response = await client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{app.Id!.Value}/bes-evidence",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
