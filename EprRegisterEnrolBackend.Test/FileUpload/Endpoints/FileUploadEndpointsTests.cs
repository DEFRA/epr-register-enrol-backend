using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.FileUpload.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.FileUpload.Endpoints;

public class FileUploadEndpointsTests : IClassFixture<FileUploadTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FileUploadTestFactory _factory;
    private readonly HttpClient _client;

    public FileUploadEndpointsTests(FileUploadTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset() => _factory.FakePersistence.Clear();

    private FileUploadModel SeedFileUpload(
        string orgId = "org-123",
        MaterialType material = MaterialType.Steel,
        int year = 2025,
        FileScanStatus scanStatus = FileScanStatus.Clean,
        Action<FileUploadModel>? configure = null
    )
    {
        var model = new FileUploadModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = orgId,
            Material = material,
            YearOfAccreditation = year,
            FileId = "cdp-upload-id",
            Filename = "test.pdf",
            ContentType = "application/pdf",
            S3Key = $"file-uploads/{material}/{year}/test.pdf",
            S3Bucket = "test-epr-register-enrol-bucket",
            ScanStatus = scanStatus,
        };
        configure?.Invoke(model);
        _factory.FakePersistence.Seed(model);
        return model;
    }

    private static CreateFileUploadRequest ValidRequest(int? yearOverride = null) =>
        new()
        {
            OrganisationId = "org-123",
            Material = MaterialType.Steel,
            YearOfAccreditation = yearOverride ?? DateTime.UtcNow.Year,
            FileId = "cdp-upload-id",
            Filename = "report.pdf",
            ContentType = "application/pdf",
            S3Key = "file-uploads/Steel/2025/report.pdf",
            S3Bucket = "test-epr-register-enrol-bucket",
        };

    // --- POST /api/v1/file-uploads ---

    [Fact]
    public async Task Create_ValidRequest_Returns201WithFileUpload()
    {
        Reset();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/file-uploads",
            ValidRequest(),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<FileUploadModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.OrganisationId.Should().Be("org-123");
        body.Material.Should().Be(MaterialType.Steel);
        body.Filename.Should().Be("report.pdf");
    }

    [Fact]
    public async Task Create_EmptyOrganisationId_Returns400()
    {
        Reset();

        var request = ValidRequest();
        request.OrganisationId = string.Empty;
        var response = await _client.PostAsJsonAsync(
            "/api/v1/file-uploads",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_InvalidContentType_Returns400()
    {
        Reset();

        var request = ValidRequest();
        request.ContentType = "application/executable";
        var response = await _client.PostAsJsonAsync(
            "/api/v1/file-uploads",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_YearTooFarInPast_Returns400()
    {
        Reset();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/file-uploads",
            ValidRequest(yearOverride: DateTime.UtcNow.Year - 5),
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // --- GET /api/v1/file-uploads ---

    [Fact]
    public async Task GetList_WithOrganisationId_Returns200WithFiles()
    {
        Reset();
        SeedFileUpload("org-list-test");

        var response = await _client.GetAsync(
            "/api/v1/file-uploads?organisationId=org-list-test",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<FileUploadModel>>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Should().HaveCount(1);
        body[0].OrganisationId.Should().Be("org-list-test");
    }

    [Fact]
    public async Task GetList_WithoutOrganisationId_Returns400()
    {
        Reset();

        var response = await _client.GetAsync(
            "/api/v1/file-uploads",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetList_WithMaterialAndYearFilter_ReturnsFilteredFiles()
    {
        Reset();
        SeedFileUpload("org-filter", MaterialType.Steel, DateTime.UtcNow.Year);
        SeedFileUpload("org-filter", MaterialType.Glass, DateTime.UtcNow.Year);

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads?organisationId=org-filter&material=Steel&year={DateTime.UtcNow.Year}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Console.WriteLine(response.Content.Headers.ContentType);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<List<FileUploadModel>>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Should().HaveCount(1);
        body[0].Material.Should().Be(MaterialType.Steel);
    }

    [Fact]
    public async Task GetList_UnknownOrg_Returns200WithEmptyList()
    {
        Reset();

        var response = await _client.GetAsync(
            "/api/v1/file-uploads?organisationId=org-nobody",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<FileUploadModel>>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Should().BeEmpty();
    }

    // --- GET /api/v1/file-uploads/{fileUploadId} ---

    [Fact]
    public async Task GetById_ExistingFile_Returns200()
    {
        Reset();
        var seeded = SeedFileUpload();

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads/{seeded.FileUploadId}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FileUploadModel>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken
        );
        body!.Filename.Should().Be("test.pdf");
    }

    [Fact]
    public async Task GetById_NonExistentFile_Returns404()
    {
        Reset();

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads/{ObjectId.GenerateNewId()}",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- GET /api/v1/file-uploads/{fileUploadId}/download ---

    [Fact]
    public async Task Download_CleanFile_Returns200WithPresignedUrl()
    {
        Reset();
        var seeded = SeedFileUpload(scanStatus: FileScanStatus.Clean);
        const string expectedUrl = "https://s3.example.com/presigned?sig=abc";
        _factory
            .MockS3Service.GeneratePresignedDownloadUrlAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(expectedUrl));

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads/{seeded.FileUploadId}/download",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("presignedUrl");
        body.Should().Contain(expectedUrl);

        await _factory
            .MockS3Service.Received(1)
            .GeneratePresignedDownloadUrlAsync(
                "test-epr-register-enrol-bucket",
                seeded.S3Key,
                seeded.Filename,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Download_FileWithoutPersistedBucket_FallsBackToGenericFilesBucketConfig()
    {
        Reset();
        var seeded = SeedFileUpload(
            scanStatus: FileScanStatus.Clean,
            configure: m => m.S3Bucket = null
        );
        _factory
            .MockS3Service.GeneratePresignedDownloadUrlAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult("https://s3.example.com/presigned?sig=abc"));

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads/{seeded.FileUploadId}/download",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockS3Service.Received(1)
            .GeneratePresignedDownloadUrlAsync(
                "file-uploads",
                seeded.S3Key,
                seeded.Filename,
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task Download_PendingFile_Returns422()
    {
        Reset();
        var seeded = SeedFileUpload(scanStatus: FileScanStatus.Pending);

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads/{seeded.FileUploadId}/download",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Download_InfectedFile_Returns422()
    {
        Reset();
        var seeded = SeedFileUpload(scanStatus: FileScanStatus.Infected);

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads/{seeded.FileUploadId}/download",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Download_NonExistentFile_Returns404()
    {
        Reset();

        var response = await _client.GetAsync(
            $"/api/v1/file-uploads/{ObjectId.GenerateNewId()}/download",
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // --- POST /api/v1/file-uploads/initiate ---

    [Fact]
    public async Task InitiateFileUpload_SendsClientSuppliedBucketAndPrefixedPath()
    {
        Reset();
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
            s3Path = "Steel/2025/report.pdf",
        };
        var response = await _client.PostAsJsonAsync(
            "/api/v1/file-uploads/initiate",
            request,
            cancellationToken: TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockCdpUploaderService.Received(1)
            .InitiateAsync(
                Arg.Is<CdpInitiateRequest>(r =>
                    r.S3Bucket == "test-epr-register-enrol-bucket"
                    && r.S3Path == "file-uploads/Steel/2025/report.pdf"
                ),
                Arg.Any<CancellationToken>()
            );
    }
}
