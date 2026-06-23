using System.Security.Cryptography;
using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

// Stub implementation — swapped for a real HTTP adapter once the Case Working Service contract is defined.
public class StubCaseWorkingApiAdapter(ILogger<StubCaseWorkingApiAdapter> logger)
    : ICaseWorkingApiAdapter
{
    public Task<string> SubmitApplicationAsync(
        AccreditationApplicationModel application,
        CancellationToken cancellationToken = default
    )
    {
        var suffix = RandomNumberGenerator.GetInt32(1_000_000_000);
        var applicationReference = $"RA-{suffix:D9}";

        logger.LogInformation(
            "StubCaseWorkingApiAdapter.SubmitApplicationAsync called for applicationId={ApplicationId} generatedRef={ApplicationReference} org={OrganisationId}",
            application.Id,
            applicationReference,
            application.OrganisationId
        );

        return Task.FromResult(applicationReference);
    }
}
