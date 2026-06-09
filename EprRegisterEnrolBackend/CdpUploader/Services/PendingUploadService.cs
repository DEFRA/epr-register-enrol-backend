using System.Collections.Concurrent;
using EprRegisterEnrolBackend.CdpUploader.Models;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

public class PendingUploadService : IPendingUploadService
{
    private record PendingUpload(string CdpStatusUrl, CdpCallbackFile? ScanResult);

    private readonly ConcurrentDictionary<string, PendingUpload> _uploads = new();

    public void Create(string fileUploadId, string cdpStatusUrl)
    {
        _uploads[fileUploadId] = new PendingUpload(cdpStatusUrl, null);
    }

    public void Complete(string fileUploadId, CdpCallbackFile fileResult)
    {
        _uploads.AddOrUpdate(
            fileUploadId,
            _ => new PendingUpload(string.Empty, fileResult),
            (_, existing) => existing with { ScanResult = fileResult }
        );
    }

    public CdpStatusResponse GetStatus(string fileUploadId)
    {
        if (!_uploads.TryGetValue(fileUploadId, out var upload))
        {
            return new CdpStatusResponse { UploadStatus = "pending" };
        }

        if (upload.ScanResult is null)
        {
            return new CdpStatusResponse { UploadStatus = "pending" };
        }

        return new CdpStatusResponse
        {
            UploadStatus = "ready",
            Form = new CdpCallbackForm { File = upload.ScanResult },
        };
    }
}
