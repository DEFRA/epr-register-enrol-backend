using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>Today's behaviour, used while DomainModel:ReadFromModel is off.</summary>
public sealed class LegacyApplicationReader(IAccreditationApplicationPersistence persistence)
    : IApplicationReader
{
    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        CancellationToken cancellationToken = default
    ) => await persistence.GetByIdAsync(organisationId, applicationId);

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    ) => (await persistence.GetByOrganisationAsync(organisationId)).ToList();

    public Task<AccreditationApplicationModel?> ServeAsync(
        AccreditationApplicationModel legacy,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<AccreditationApplicationModel?>(legacy);
}
