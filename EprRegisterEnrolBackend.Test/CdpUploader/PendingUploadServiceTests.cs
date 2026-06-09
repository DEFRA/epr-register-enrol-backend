using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.CdpUploader;

public class PendingUploadServiceTests
{
    private readonly PendingUploadService _sut = new();

    [Fact]
    public void GetStatus_UnknownId_ReturnsPending()
    {
        var result = _sut.GetStatus("does-not-exist");

        result.UploadStatus.Should().Be("pending");
        result.Form.Should().BeNull();
    }

    [Fact]
    public void Create_ThenGetStatus_ReturnsPending()
    {
        _sut.Create("upload-1", "http://cdp/status/1");

        var result = _sut.GetStatus("upload-1");

        result.UploadStatus.Should().Be("pending");
        result.Form.Should().BeNull();
    }

    [Fact]
    public void Complete_ThenGetStatus_ReturnsReadyWithFileResult()
    {
        _sut.Create("upload-2", "http://cdp/status/2");
        var file = new CdpCallbackFile
        {
            FileId = "file-abc",
            Filename = "test.csv",
            FileStatus = "complete",
            S3Bucket = "my-bucket",
            S3Key = "uploads/test.csv",
            ContentType = "text/csv",
        };

        _sut.Complete("upload-2", file);

        var result = _sut.GetStatus("upload-2");
        result.UploadStatus.Should().Be("ready");
        result.Form.Should().NotBeNull();
        result.Form!.File.Should().BeEquivalentTo(file);
    }

    [Fact]
    public void Complete_WithoutPriorCreate_StillStoresResult()
    {
        var file = new CdpCallbackFile { FileId = "orphan", Filename = "orphan.pdf" };

        _sut.Complete("upload-orphan", file);

        var result = _sut.GetStatus("upload-orphan");
        result.UploadStatus.Should().Be("ready");
        result.Form!.File!.FileId.Should().Be("orphan");
    }

    [Fact]
    public void Complete_OverwritesPreviousScanResult()
    {
        _sut.Create("upload-3", "http://cdp/status/3");
        _sut.Complete("upload-3", new CdpCallbackFile { FileId = "first" });
        _sut.Complete("upload-3", new CdpCallbackFile { FileId = "second" });

        var result = _sut.GetStatus("upload-3");
        result.Form!.File!.FileId.Should().Be("second");
    }
}
