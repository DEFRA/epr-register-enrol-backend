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
    private HttpClient CreateClientWithFailingUpdate(AccreditationApplicationModel application)
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
                services.AddSingleton(brokenPersistence)
            )
        );
        return factoryWithBrokenPersistence.CreateClient();
    }

    // --- AddBesEvidenceFile ---

    [Fact]
    public async Task AddBesEvidenceFile_ApplicationNotFound_Returns404()
    {
        Reset();

        var request = new AddBesEvidenceFileRequest
        {
            FileId = "bes-file-404",
            Filename = "evidence.pdf",
            S3Key = "bes-evidence/bes-file-404",
        };
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

        var request = new AddBesEvidenceFileRequest
        {
            FileId = "bes-file-405",
            Filename = "evidence.pdf",
            S3Key = "bes-evidence/bes-file-405",
        };
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

        var request = new AddBesEvidenceFileRequest
        {
            FileId = "bes-file-new",
            Filename = "new.pdf",
            S3Key = "bes-evidence/bes-file-new",
        };
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
        using var client = CreateClientWithFailingUpdate(app);

        var request = new AddBesEvidenceFileRequest
        {
            FileId = "bes-file-fail",
            Filename = "evidence.pdf",
            S3Key = "bes-evidence/bes-file-fail",
        };
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
