using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.ReEx;

namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

/// <summary>
/// Registered only when <c>TestOrganisations__Enabled=true</c> outside the global stub switch
/// (see <see cref="ReExApiAdapterRegistration"/>): every call for an <c>e2e-</c> test
/// organisation goes to the stub adapter, everything else to the real one. Lets E2E tests on a
/// deployed environment (CDP test) drive the operator journey for organisations ReEx has never
/// heard of, without stubbing ReEx for real organisations.
/// </summary>
public class TestOrganisationReExApiAdapter(
    IReExApiAdapter realAdapter,
    IReExApiAdapter testOrganisationAdapter
) : IReExApiAdapter
{
    private IReExApiAdapter For(string organisationId) =>
        TestOrganisationsConfig.IsTestOrganisation(organisationId)
            ? testOrganisationAdapter
            : realAdapter;

    public Task<ReExResult<ReExAccreditationDto>> GetAccreditationAsync(
        string organisationId,
        string registrationId,
        MaterialType materialType,
        int year
    ) => For(organisationId).GetAccreditationAsync(organisationId, registrationId, materialType, year);

    public Task<ReExResult<LinkedDefraOrganisationResult>> GetLinkedDefraOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    ) => For(organisationId).GetLinkedDefraOrganisationAsync(organisationId, cancellationToken);

    public Task<ReExResult<int?>> GetOrganisationNumberAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    ) => For(organisationId).GetOrganisationNumberAsync(organisationId, cancellationToken);

    public Task<ReExResult<Nation>> GetNationAsync(
        string organisationId,
        string registrationId,
        CancellationToken cancellationToken = default
    ) => For(organisationId).GetNationAsync(organisationId, registrationId, cancellationToken);
}
