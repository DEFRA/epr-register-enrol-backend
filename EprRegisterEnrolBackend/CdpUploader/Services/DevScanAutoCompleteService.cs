using EprRegisterEnrolBackend.CdpUploader.Models;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

// Completes pending file uploads in Development by polling CDP Uploader's real
// status endpoint directly, since its scan-complete webhook callback can't reach
// this backend locally (it targets a host/route only reachable from outside this
// Docker network). Polling — rather than faking the result — means local dev
// exercises the same file metadata (real filename, fileId) and S3 key shape that
// production gets from the real callback.
public class DevScanAutoCompleteService(
    IPendingUploadService pendingUploadService,
    ICdpUploaderService cdpUploaderService,
    ILogger<DevScanAutoCompleteService> logger
) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "DevScanAutoCompleteService started — polling CDP uploader status for pending uploads"
        );
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var pendingIds = pendingUploadService.GetPendingUploadIds();

            foreach (var fileUploadId in pendingIds)
            {
                try
                {
                    await TryCompleteFromCdpStatus(fileUploadId, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning(
                        ex,
                        "DevScanAutoComplete: unexpected error completing upload {FileUploadId}",
                        fileUploadId
                    );
                }
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    internal async Task TryCompleteFromCdpStatus(
        string fileUploadId,
        CancellationToken cancellationToken
    )
    {
        var details = pendingUploadService.TryGetPendingUploadDetails(fileUploadId);
        if (details is null)
            return;

        CdpStatusResponse? cdpStatus;
        try
        {
            cdpStatus = await cdpUploaderService.GetStatusAsync(
                details.CdpStatusUrl,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "DevScanAutoComplete: failed to poll CDP status for upload {FileUploadId}",
                fileUploadId
            );
            return;
        }

        if (cdpStatus?.UploadStatus != "ready")
            return;

        var file = cdpStatus.Form?.File;
        if (file is null)
            return;

        // Use CDP's own per-file scan outcome, not cdpStatus.ProcessingStatus — that field
        // only ever gets populated by this backend's *own* status endpoint (see
        // PendingUploadService.GetStatus), never by the real CDP uploader response this
        // is deserialised from, so comparing it here always evaluated as "rejected" and
        // could race the real webhook callback into clobbering a clean scan result.
        var fileStatus = file.FileStatus ?? "rejected";
        var s3Key =
            details.S3Path is not null && details.CdpUploadId is not null
                ? $"{details.S3Path.TrimEnd('/')}/{details.CdpUploadId}/{file.FileId}"
                : null;

        var result = new CdpCallbackFile
        {
            FileId = file.FileId,
            Filename = file.Filename,
            FileStatus = fileStatus,
            ContentType = file.ContentType ?? file.DetectedContentType,
            S3Bucket = details.S3Bucket,
            S3Key = s3Key,
        };

        pendingUploadService.Complete(fileUploadId, result);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "DevScanAutoComplete: completed upload {FileUploadId} from real CDP status (fileStatus={FileStatus})",
                fileUploadId,
                fileStatus
            );
        }
    }
}
