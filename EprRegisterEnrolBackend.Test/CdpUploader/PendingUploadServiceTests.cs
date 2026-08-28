using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.CdpUploader;

/// <summary>
/// Exercises the Mongo-backed <see cref="PendingUploadService"/> against a real
/// ephemeral mongod (via <see cref="MongoIntegrationFixture"/>), rather than the
/// old ConcurrentDictionary-backed unit tests - the whole point of this class is
/// that state now lives in Mongo, so a fake in-memory store would not prove
/// anything about persistence surviving a restart or being shared across
/// instances (epr-register-enrol-backend-6y2).
/// </summary>
public sealed class PendingUploadServiceTests : IDisposable
{
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly PendingUploadService _sut;

    public PendingUploadServiceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("pending_uploads");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _sut = new PendingUploadService(_factory, NullLoggerFactory.Instance);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    [Fact]
    public async Task GetStatusAsync_UnknownId_ReturnsPendingPreprocessing()
    {
        var result = await _sut.GetStatusAsync(
            "does-not-exist",
            TestContext.Current.CancellationToken
        );

        result.UploadStatus.Should().Be("pending");
        result.ProcessingStatus.Should().Be("preprocessing");
        result.Form.Should().BeNull();
    }

    [Fact]
    public async Task CreateAsync_ThenGetStatusAsync_ReturnsPendingPreprocessing()
    {
        await _sut.CreateAsync(
            "upload-1",
            "http://cdp/status/1",
            ct: TestContext.Current.CancellationToken
        );

        var result = await _sut.GetStatusAsync("upload-1", TestContext.Current.CancellationToken);

        result.UploadStatus.Should().Be("pending");
        result.ProcessingStatus.Should().Be("preprocessing");
        result.Form.Should().BeNull();
    }

    [Fact]
    public async Task CompleteAsync_WithCompleteStatus_ReturnsReadyValidated()
    {
        await _sut.CreateAsync(
            "upload-2",
            "http://cdp/status/2",
            ct: TestContext.Current.CancellationToken
        );
        var file = new CdpCallbackFile
        {
            FileId = "file-abc",
            Filename = "test.csv",
            FileStatus = "complete",
            S3Bucket = "my-bucket",
            S3Key = "uploads/test.csv",
            ContentType = "text/csv",
        };

        await _sut.CompleteAsync("upload-2", file, TestContext.Current.CancellationToken);

        var result = await _sut.GetStatusAsync("upload-2", TestContext.Current.CancellationToken);
        result.UploadStatus.Should().Be("ready");
        result.ProcessingStatus.Should().Be("validated");
        result.Form.Should().NotBeNull();
        result.Form!.File.Should().BeEquivalentTo(file);
    }

    [Fact]
    public async Task CompleteAsync_WithRejectedStatus_ReturnsReadyRejected()
    {
        await _sut.CreateAsync(
            "upload-r",
            "http://cdp/status/r",
            ct: TestContext.Current.CancellationToken
        );
        var file = new CdpCallbackFile
        {
            FileId = "file-r",
            Filename = "virus.pdf",
            FileStatus = "rejected",
        };

        await _sut.CompleteAsync("upload-r", file, TestContext.Current.CancellationToken);

        var result = await _sut.GetStatusAsync("upload-r", TestContext.Current.CancellationToken);
        result.UploadStatus.Should().Be("ready");
        result.ProcessingStatus.Should().Be("rejected");
        result.Form!.File!.FileId.Should().Be("file-r");
    }

    [Fact]
    public async Task CompleteAsync_WithoutPriorCreate_StillStoresResult()
    {
        var file = new CdpCallbackFile
        {
            FileId = "orphan",
            Filename = "orphan.pdf",
            FileStatus = "complete",
        };

        await _sut.CompleteAsync("upload-orphan", file, TestContext.Current.CancellationToken);

        var result = await _sut.GetStatusAsync(
            "upload-orphan",
            TestContext.Current.CancellationToken
        );
        result.UploadStatus.Should().Be("ready");
        result.ProcessingStatus.Should().Be("validated");
        result.Form!.File!.FileId.Should().Be("orphan");
    }

    [Fact]
    public async Task CompleteAsync_OverwritesPreviousScanResult()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.CreateAsync("upload-3", "http://cdp/status/3", ct: ct);
        await _sut.CompleteAsync(
            "upload-3",
            new CdpCallbackFile { FileId = "first", FileStatus = "complete" },
            ct
        );
        await _sut.CompleteAsync(
            "upload-3",
            new CdpCallbackFile { FileId = "second", FileStatus = "complete" },
            ct
        );

        var result = await _sut.GetStatusAsync("upload-3", ct);
        result.Form!.File!.FileId.Should().Be("second");
        result.ProcessingStatus.Should().Be("validated");
    }

    [Fact]
    public async Task TryGetPendingUploadDetailsAsync_PreprocessingUpload_ReturnsDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.CreateAsync(
            "upload-4",
            "http://cdp/status/4",
            cdpUploadId: "cdp-4",
            s3Bucket: "bucket",
            s3Path: "path",
            ct: ct
        );

        var details = await _sut.TryGetPendingUploadDetailsAsync("upload-4", ct);

        details.Should().NotBeNull();
        details!.CdpStatusUrl.Should().Be("http://cdp/status/4");
        details.CdpUploadId.Should().Be("cdp-4");
        details.S3Bucket.Should().Be("bucket");
        details.S3Path.Should().Be("path");
    }

    [Fact]
    public async Task TryGetPendingUploadDetailsAsync_CompletedUpload_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.CreateAsync("upload-5", "http://cdp/status/5", ct: ct);
        await _sut.CompleteAsync(
            "upload-5",
            new CdpCallbackFile { FileId = "f", FileStatus = "complete" },
            ct
        );

        var details = await _sut.TryGetPendingUploadDetailsAsync("upload-5", ct);

        details.Should().BeNull();
    }

    [Fact]
    public async Task GetPendingUploadIdsAsync_OnlyReturnsPreprocessingIds()
    {
        var ct = TestContext.Current.CancellationToken;
        await _sut.CreateAsync("upload-pending", "http://cdp/status/pending", ct: ct);
        await _sut.CreateAsync("upload-done", "http://cdp/status/done", ct: ct);
        await _sut.CompleteAsync(
            "upload-done",
            new CdpCallbackFile { FileId = "f", FileStatus = "complete" },
            ct
        );

        var ids = await _sut.GetPendingUploadIdsAsync(ct);

        ids.Should().ContainSingle().Which.Should().Be("upload-pending");
    }

    // Simulates two backend instances behind the same Mongo - the actual point of
    // this migration (epr-register-enrol-backend-6y2 acceptance criteria).
    [Fact]
    public async Task TwoServiceInstances_SharedMongo_SeeEachOthersWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherInstance = new PendingUploadService(_factory, NullLoggerFactory.Instance);

        await _sut.CreateAsync("upload-shared", "http://cdp/status/shared", ct: ct);
        var details = await otherInstance.TryGetPendingUploadDetailsAsync("upload-shared", ct);
        details.Should().NotBeNull();
        details!.CdpStatusUrl.Should().Be("http://cdp/status/shared");

        await otherInstance.CompleteAsync(
            "upload-shared",
            new CdpCallbackFile { FileId = "f", FileStatus = "complete" },
            ct
        );
        var status = await _sut.GetStatusAsync("upload-shared", ct);
        status.ProcessingStatus.Should().Be("validated");
    }

    // Proxy for "state survives a restart": a second instance constructed after the
    // first goes out of scope (nothing in this service holds process-only state)
    // still reads what the first wrote.
    [Fact]
    public async Task NewServiceInstance_ReadsStateWrittenByEarlierInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstInstance = new PendingUploadService(_factory, NullLoggerFactory.Instance);
        await firstInstance.CreateAsync("upload-restart", "http://cdp/status/restart", ct: ct);

        var afterRestart = new PendingUploadService(_factory, NullLoggerFactory.Instance);
        var result = await afterRestart.GetStatusAsync("upload-restart", ct);

        result.UploadStatus.Should().Be("pending");
        result.ProcessingStatus.Should().Be("preprocessing");
    }

    [Fact]
    public async Task Constructor_CreatesExpiresAtTtlIndexAndStatusIndex()
    {
        var indexes = await ListIndexesAsync();

        var expiresAtIndex = indexes.Single(i => i["key"].AsBsonDocument.Contains("expiresAt"));
        expiresAtIndex.GetValue("expireAfterSeconds", -1).ToInt64().Should().Be(0);

        indexes.Should().Contain(i => i["key"].AsBsonDocument.Contains("status"));
    }

    [Fact]
    public void Constructor_is_idempotent_when_the_indexes_already_match()
    {
        var ex = Record.Exception(() =>
            new PendingUploadService(_factory, NullLoggerFactory.Instance)
        );

        ex.Should().BeNull();
    }

    private async Task<List<MongoDB.Bson.BsonDocument>> ListIndexesAsync()
    {
        var collection = _factory.GetCollection<PendingUploadDocument>("pendingUploads");
        return await (
            await collection.Indexes.ListAsync(TestContext.Current.CancellationToken)
        ).ToListAsync(TestContext.Current.CancellationToken);
    }
}
