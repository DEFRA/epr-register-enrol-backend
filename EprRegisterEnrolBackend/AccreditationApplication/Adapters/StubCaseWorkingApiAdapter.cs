using System.Security.Cryptography;
using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// Stub implementation — swapped for a real HTTP adapter once the Case Working Service contract is defined.
public class StubCaseWorkingApiAdapter(ILogger<StubCaseWorkingApiAdapter> logger)
    : ICaseWorkingApiAdapter
{
    public Task<CaseWorkingSubmissionResult> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        var suffix = RandomNumberGenerator.GetInt32(1_000_000_000);
        var applicationReference = $"RA-{suffix:D9}";
        var workItemId = Guid.NewGuid();

        logger.LogInformation(
            "StubCaseWorkingApiAdapter.SubmitApplicationAsync called for applicationId={ApplicationId} generatedRef={ApplicationReference} workItemId={WorkItemId} org={OrganisationId}",
            application.Id,
            applicationReference,
            workItemId,
            application.OrganisationId
        );

        return Task.FromResult(new CaseWorkingSubmissionResult(applicationReference, workItemId));
    }
}
