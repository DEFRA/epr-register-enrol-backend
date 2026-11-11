using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// The CDP Uploader callback route is unauthenticated, so anyone can call it. These run it
// against the real Mongo-backed PendingUploadService to prove a callback for an id this
// service never started (or whose record expired) stores nothing and logs an Error, while
// still being acknowledged with 200 so CDP Uploader does not retry it.
public sealed class UploadCompletedEndpointTests : IDisposable
{
    private const string Route = "/api/v1/accreditation-applications/files/upload-completed";

    private sealed class RealPendingUploadsFactory(IPendingUploadService pendingUploads)
        : AccreditationApplicationTestFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton(pendingUploads)
            );
        }
    }

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _mongo;
    private readonly CapturingLoggerFactory _logs = new();
    private readonly PendingUploadService _pendingUploads;
    private readonly RealPendingUploadsFactory _factory;
    private readonly HttpClient _client;

    public UploadCompletedEndpointTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("upload_completed");
        _mongo = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _pendingUploads = new PendingUploadService(_mongo, _logs);
        _factory = new RealPendingUploadsFactory(_pendingUploads);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _mongo.GetClient().DropDatabase(_databaseName);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoCollection<PendingUploadDocument> Collection() =>
        _mongo.GetCollection<PendingUploadDocument>("pendingUploads");

    // Shaped like a real CDP callback: a clean scan with an S3 location.
    private static object CallbackFor(string fileUploadId) =>
        new
        {
            uploadStatus = "ready",
            form = new
            {
                file = new
                {
                    fileId = "file-xyz",
                    filename = "plan.pdf",
                    fileStatus = "complete",
                    contentType = "application/pdf",
                    s3Bucket = "some-bucket",
                    s3Key = "some/path/file-xyz",
                },
            },
            metadata = new Dictionary<string, string> { ["fileUploadId"] = fileUploadId },
        };

    [Fact]
    public async Task UnknownFileUploadId_Returns200_StoresNothing_LogsErrorWithId()
    {
        var fileUploadId = Guid.NewGuid().ToString();

        var response = await _client.PostAsJsonAsync(Route, CallbackFor(fileUploadId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Collection().CountDocumentsAsync(_ => true, cancellationToken: Ct)).Should().Be(0);
        var error = _logs.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Subject;
        error.Properties["FileUploadId"].Should().Be(fileUploadId);
    }

    [Fact]
    public async Task KnownFileUploadId_Returns200_StoresScanResult()
    {
        var fileUploadId = Guid.NewGuid().ToString();
        await _pendingUploads.CreateAsync(fileUploadId, "http://cdp/status", ct: Ct);

        var response = await _client.PostAsJsonAsync(Route, CallbackFor(fileUploadId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await Collection().Find(u => u.Id == fileUploadId).SingleAsync(Ct);
        stored.Status.Should().Be(FileProcessingStatus.Validated);
        stored.ScanResult!.FileId.Should().Be("file-xyz");
        stored.CdpStatusUrl.Should().Be("http://cdp/status");
        _logs.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
    }
}
