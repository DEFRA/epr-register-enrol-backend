using EprRegisterEnrolBackend.CdpUploader.Models;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

public interface IPendingUploadService
{
    void Create(string fileUploadId, string cdpStatusUrl);
    void Complete(string fileUploadId, CdpCallbackFile fileResult);
    CdpStatusResponse GetStatus(string fileUploadId);
}
