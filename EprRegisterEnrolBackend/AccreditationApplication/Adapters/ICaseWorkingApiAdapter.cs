using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public interface ICaseWorkingApiAdapter
{
    /// <summary>
    /// Creates a work item in the case management service for the submitted application.
    /// Returns the case management application reference (RA-*) on success, or null if
    /// the reference could not be retrieved from the response.
    /// </summary>
    Task<string?> SubmitApplicationAsync(AccreditationApplicationModel application, CancellationToken cancellationToken = default);
}
