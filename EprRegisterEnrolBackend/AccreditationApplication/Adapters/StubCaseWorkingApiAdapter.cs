using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// TODO: implement client
// Stub implementation — swapped for a real HTTP adapter once the Case Working Service contract is defined.
public class StubCaseWorkingApiAdapter(ILogger<StubCaseWorkingApiAdapter> logger) : ICaseWorkingApiAdapter
{
    public Task SubmitApplicationAsync(AccreditationApplicationModel application)
    {
        logger.LogInformation(
            "StubCaseWorkingApiAdapter.SubmitApplicationAsync called for applicationId={ApplicationId} ref={ApplicationReference} org={OrganisationId}",
            application.Id, application.ApplicationReference, application.OrganisationId);

        return Task.CompletedTask;
    }
}
