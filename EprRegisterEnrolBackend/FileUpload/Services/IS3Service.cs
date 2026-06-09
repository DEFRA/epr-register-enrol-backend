namespace EprRegisterEnrolBackend.FileUpload.Services;

public interface IS3Service
{
    Task<string> GeneratePresignedDownloadUrlAsync(
        string bucket,
        string s3Key,
        string filename,
        CancellationToken cancellationToken = default
    );
}
