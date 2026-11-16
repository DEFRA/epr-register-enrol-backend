using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>
/// Serves the application persistence as it stands. With DomainModel:ReadFromModel off that is legacy; after the cut
/// (DomainModel:LegacyWrites off) it is the model-backed persistence, which already projects from the model with the
/// model's version and timestamps (RA-706).
/// </summary>
public sealed class PersistenceApplicationReader(IAccreditationApplicationPersistence persistence)
    : IApplicationReader
{
    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await persistence.GetByIdAsync(organisationId, applicationId);
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await persistence.GetByOrganisationAsync(organisationId)).ToList();
    }

    public Task<AccreditationApplicationModel?> ServeFromSourceAsync(
        AccreditationApplicationModel legacy,
        CancellationToken cancellationToken = default
    ) => Task.FromResult<AccreditationApplicationModel?>(legacy);

    public Task<IReadOnlyList<AccreditationApplicationModel>> ServeManyFromSourceAsync(
        IReadOnlyList<AccreditationApplicationModel> legacy,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AccreditationApplicationModel>>(legacy);
    }
}
