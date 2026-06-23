using EprRegisterEnrolBackend.CdpUploader.Models;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

// Auto-completes pending file uploads in Development where the CDP uploader's
// mock virus scan cannot call back (SQS queues don't exist in Floci).
public class DevScanAutoCompleteService(
    IPendingUploadService pendingUploadService,
    ILogger<DevScanAutoCompleteService> logger
) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "DevScanAutoCompleteService started — pending uploads will auto-complete"
        );
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var pendingIds = pendingUploadService.GetPendingUploadIds();

            foreach (var fileUploadId in pendingIds)
            {
                var fakeResult = new CdpCallbackFile
                {
                    FileId = fileUploadId,
                    Filename = "dev-auto-completed.pdf",
                    FileStatus = "complete",
                    ContentType = "application/pdf",
                };

                pendingUploadService.Complete(fileUploadId, fakeResult);
                logger.LogInformation(
                    "DevScanAutoComplete: auto-completed upload {FileUploadId}",
                    fileUploadId
                );
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
