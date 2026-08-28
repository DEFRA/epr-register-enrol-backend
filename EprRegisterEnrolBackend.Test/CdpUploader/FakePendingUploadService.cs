using System.Collections.Concurrent;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.CdpUploader.Services;

namespace EprRegisterEnrolBackend.Test.CdpUploader;

// In-memory stand-in for the real Mongo-backed PendingUploadService, so endpoint tests
// don't need a real Mongo instance - mirrors FakeRegulatoryNumberSequenceCounterPersistence.
// Behaviourally identical to the old ConcurrentDictionary-backed production implementation
// this replaced (epr-register-enrol-backend-6y2); TTL/multi-instance behaviour is covered
// separately by PendingUploadServiceTests against a real ephemeral mongod.
public class FakePendingUploadService : IPendingUploadService
{
    private record PendingUpload(
        string CdpStatusUrl,
        string? CdpUploadId,
        string? S3Bucket,
        string? S3Path,
        CdpCallbackFile? ScanResult,
        FileProcessingStatus Status
    );

    private readonly ConcurrentDictionary<string, PendingUpload> _uploads = new();

    public void Clear() => _uploads.Clear();

    public Task CreateAsync(
        string fileUploadId,
        string cdpStatusUrl,
        string? cdpUploadId = null,
        string? s3Bucket = null,
        string? s3Path = null,
        CancellationToken ct = default
    )
    {
        _uploads[fileUploadId] = new PendingUpload(
            cdpStatusUrl,
            cdpUploadId,
            s3Bucket,
            s3Path,
            null,
            FileProcessingStatus.Preprocessing
        );
        return Task.CompletedTask;
    }

    public Task<PendingUploadDetails?> TryGetPendingUploadDetailsAsync(
        string fileUploadId,
        CancellationToken ct = default
    )
    {
        if (
            !_uploads.TryGetValue(fileUploadId, out var upload)
            || upload.Status != FileProcessingStatus.Preprocessing
        )
            return Task.FromResult<PendingUploadDetails?>(null);

        return Task.FromResult<PendingUploadDetails?>(
            new PendingUploadDetails(
                upload.CdpStatusUrl,
                upload.CdpUploadId,
                upload.S3Bucket,
                upload.S3Path
            )
        );
    }

    public Task CompleteAsync(
        string fileUploadId,
        CdpCallbackFile fileResult,
        CancellationToken ct = default
    )
    {
        var newStatus =
            fileResult.FileStatus == "complete"
                ? FileProcessingStatus.Validated
                : FileProcessingStatus.Rejected;

        _uploads.AddOrUpdate(
            fileUploadId,
            _ => new PendingUpload(string.Empty, null, null, null, fileResult, newStatus),
            (_, existing) => existing with { ScanResult = fileResult, Status = newStatus }
        );

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetPendingUploadIdsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> ids = _uploads
            .Where(kvp => kvp.Value.Status == FileProcessingStatus.Preprocessing)
            .Select(kvp => kvp.Key)
            .ToList();
        return Task.FromResult(ids);
    }

    public Task<CdpStatusResponse> GetStatusAsync(
        string fileUploadId,
        CancellationToken ct = default
    )
    {
        if (!_uploads.TryGetValue(fileUploadId, out var upload))
        {
            return Task.FromResult(
                new CdpStatusResponse
                {
                    UploadStatus = "pending",
                    ProcessingStatus = "preprocessing",
                }
            );
        }

        CdpStatusResponse response = upload.Status switch
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

        return Task.FromResult(response);
    }
}
