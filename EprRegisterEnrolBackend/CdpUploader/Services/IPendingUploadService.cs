using EprRegisterEnrolBackend.CdpUploader.Models;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

public record PendingUploadDetails(
    string CdpStatusUrl,
    string? CdpUploadId,
    string? S3Bucket,
    string? S3Path
);

public interface IPendingUploadService
{
    Task CreateAsync(
        string fileUploadId,
        string cdpStatusUrl,
        string? cdpUploadId = null,
        string? s3Bucket = null,
        string? s3Path = null,
        CancellationToken ct = default
    );
    Task CompleteAsync(
        string fileUploadId,
        CdpCallbackFile fileResult,
        CancellationToken ct = default
    );
    Task<CdpStatusResponse> GetStatusAsync(string fileUploadId, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPendingUploadIdsAsync(CancellationToken ct = default);
    Task<PendingUploadDetails?> TryGetPendingUploadDetailsAsync(
        string fileUploadId,
        CancellationToken ct = default
    );
}
