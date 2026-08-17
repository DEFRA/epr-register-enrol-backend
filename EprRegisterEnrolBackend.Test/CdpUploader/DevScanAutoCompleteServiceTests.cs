using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.CdpUploader;

public class DevScanAutoCompleteServiceTests
{
    private readonly IPendingUploadService _pendingUploadService =
        Substitute.For<IPendingUploadService>();
    private readonly ICdpUploaderService _cdpUploaderService =
        Substitute.For<ICdpUploaderService>();

    private DevScanAutoCompleteService BuildSut() =>
        new(
            _pendingUploadService,
            _cdpUploaderService,
            NullLogger<DevScanAutoCompleteService>.Instance
        );

    // Regression test for a race that clobbered clean scan results: the real CDP uploader
    // response never populates CdpStatusResponse.ProcessingStatus (that field only exists on
    // the shape *this backend* returns from its own status endpoint — see
    // PendingUploadServiceTests). Reading it here previously meant every upload this poller
    // completed was marked "rejected", regardless of the actual scan outcome, and whichever
    // of the poller or the real webhook callback landed last would win.
    [Fact]
    public async Task TryCompleteFromCdpStatus_RealCdpResponseShape_CompletesAsCleanNotRejected()
    {
        _pendingUploadService
            .TryGetPendingUploadDetails("upload-1")
            .Returns(
                new PendingUploadDetails(
                    "http://cdp-uploader/status/upload-1",
                    "cdp-upload-1",
                    "my-bucket",
                    "sampling-plans/accreditation/sampling-plan/app-1"
                )
            );

        // Shaped exactly like the real CDP uploader's JSON: UploadStatus set, ProcessingStatus
        // left at its C# default because the real service never sends that field, and the
        // actual scan outcome living on Form.File.FileStatus.
        _cdpUploaderService
            .GetStatusAsync("http://cdp-uploader/status/upload-1", Arg.Any<CancellationToken>())
            .Returns(
                new CdpStatusResponse
                {
                    UploadStatus = "ready",
                    Form = new CdpCallbackForm
                    {
                        File = new CdpCallbackFile
                        {
                            FileId = "file-abc",
                            Filename = "business-plan.pdf",
                            FileStatus = "complete",
                            ContentType = "application/pdf",
                        },
                    },
                }
            );

        var sut = BuildSut();
        await sut.TryCompleteFromCdpStatus("upload-1", CancellationToken.None);

        _pendingUploadService
            .Received(1)
            .Complete(
                "upload-1",
                Arg.Is<CdpCallbackFile>(f => f.FileId == "file-abc" && f.FileStatus == "complete")
            );
    }

    [Fact]
    public async Task TryCompleteFromCdpStatus_RealRejectedFile_CompletesAsRejected()
    {
        _pendingUploadService
            .TryGetPendingUploadDetails("upload-2")
            .Returns(
                new PendingUploadDetails("http://cdp-uploader/status/upload-2", null, null, null)
            );

        _cdpUploaderService
            .GetStatusAsync("http://cdp-uploader/status/upload-2", Arg.Any<CancellationToken>())
            .Returns(
                new CdpStatusResponse
                {
                    UploadStatus = "ready",
                    Form = new CdpCallbackForm
                    {
                        File = new CdpCallbackFile
                        {
                            FileId = "file-virus",
                            FileStatus = "rejected",
                        },
                    },
                }
            );

        var sut = BuildSut();
        await sut.TryCompleteFromCdpStatus("upload-2", CancellationToken.None);

        _pendingUploadService
            .Received(1)
            .Complete("upload-2", Arg.Is<CdpCallbackFile>(f => f.FileStatus == "rejected"));
    }

    [Fact]
    public async Task TryCompleteFromCdpStatus_UploadNotPending_DoesNotPollOrComplete()
    {
        _pendingUploadService
            .TryGetPendingUploadDetails("upload-3")
            .Returns((PendingUploadDetails?)null);

        var sut = BuildSut();
        await sut.TryCompleteFromCdpStatus("upload-3", CancellationToken.None);

        await _cdpUploaderService
            .DidNotReceive()
            .GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _pendingUploadService
            .DidNotReceive()
            .Complete(Arg.Any<string>(), Arg.Any<CdpCallbackFile>());
    }

    [Fact]
    public async Task TryCompleteFromCdpStatus_CdpNotReadyYet_DoesNotComplete()
    {
        _pendingUploadService
            .TryGetPendingUploadDetails("upload-4")
            .Returns(
                new PendingUploadDetails("http://cdp-uploader/status/upload-4", null, null, null)
            );
        _cdpUploaderService
            .GetStatusAsync("http://cdp-uploader/status/upload-4", Arg.Any<CancellationToken>())
            .Returns(new CdpStatusResponse { UploadStatus = "pending" });

        var sut = BuildSut();
        await sut.TryCompleteFromCdpStatus("upload-4", CancellationToken.None);

        _pendingUploadService
            .DidNotReceive()
            .Complete(Arg.Any<string>(), Arg.Any<CdpCallbackFile>());
    }
}
