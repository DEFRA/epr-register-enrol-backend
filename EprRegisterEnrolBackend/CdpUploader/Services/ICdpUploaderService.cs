using EprRegisterEnrolBackend.CdpUploader.Models;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

public interface ICdpUploaderService
{
    /// <summary>
    /// Call CDP /initiate, rewrites internal hostnames so URLs are browser-reachable,
    /// and returns uploadUrl + statusUrl pointing at the CDP service.
    /// </summary>
    Task<CdpInitiateResponse> InitiateAsync(
        CdpInitiateRequest request,
        CancellationToken cancellationToken = default
    );
}
