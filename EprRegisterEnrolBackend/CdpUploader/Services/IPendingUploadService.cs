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
    void Create(
        string fileUploadId,
        string cdpStatusUrl,
        string? cdpUploadId = null,
        string? s3Bucket = null,
        string? s3Path = null
    );
    void Complete(string fileUploadId, CdpCallbackFile fileResult);
    CdpStatusResponse GetStatus(string fileUploadId);
    IReadOnlyList<string> GetPendingUploadIds();
    PendingUploadDetails? TryGetPendingUploadDetails(string fileUploadId);
}
