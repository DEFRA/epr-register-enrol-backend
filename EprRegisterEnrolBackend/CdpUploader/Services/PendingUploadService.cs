using System.Collections.Concurrent;
using EprRegisterEnrolBackend.CdpUploader.Models;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

public class PendingUploadService(ILogger<PendingUploadService> logger) : IPendingUploadService
{
    private record PendingUpload(
        string CdpStatusUrl,
        CdpCallbackFile? ScanResult,
        FileProcessingStatus Status
    );

    private readonly ConcurrentDictionary<string, PendingUpload> _uploads = new();

    public void Create(string fileUploadId, string cdpStatusUrl)
    {
        _uploads[fileUploadId] = new PendingUpload(
            cdpStatusUrl,
            null,
            FileProcessingStatus.Preprocessing
        );
        logger.LogInformation(
            "Upload {FileUploadId} state → {Status}",
            fileUploadId,
            FileProcessingStatus.Preprocessing
        );
    }

    public void Complete(string fileUploadId, CdpCallbackFile fileResult)
    {
        var newStatus =
            fileResult.FileStatus == "complete"
                ? FileProcessingStatus.Validated
                : FileProcessingStatus.Rejected;

        _uploads.AddOrUpdate(
            fileUploadId,
            _ => new PendingUpload(string.Empty, fileResult, newStatus),
            (_, existing) => existing with { ScanResult = fileResult, Status = newStatus }
        );

        logger.LogInformation(
            "Upload {FileUploadId} state → {Status} (fileStatus={FileStatus})",
            fileUploadId,
            newStatus,
            fileResult.FileStatus
        );
    }

    public IReadOnlyList<string> GetPendingUploadIds()
    {
        return _uploads
            .Where(kvp => kvp.Value.Status == FileProcessingStatus.Preprocessing)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    public CdpStatusResponse GetStatus(string fileUploadId)
    {
        if (!_uploads.TryGetValue(fileUploadId, out var upload))
        {
            return new CdpStatusResponse
            {
                UploadStatus = "pending",
                ProcessingStatus = "preprocessing",
            };
        }

        return upload.Status switch
        {
            FileProcessingStatus.Preprocessing => new CdpStatusResponse
            {
                UploadStatus = "pending",
                ProcessingStatus = "preprocessing",
            },
            FileProcessingStatus.Validated => new CdpStatusResponse
            {
                UploadStatus = "ready",
                ProcessingStatus = "validated",
                Form = new CdpCallbackForm { File = upload.ScanResult },
            },
            FileProcessingStatus.Rejected => new CdpStatusResponse
            {
                UploadStatus = "ready",
                ProcessingStatus = "rejected",
                Form = new CdpCallbackForm { File = upload.ScanResult },
            },
            _ => new CdpStatusResponse
            {
                UploadStatus = "pending",
                ProcessingStatus = "preprocessing",
            },
        };
    }
}
