using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>
/// RA-706: reads after the cut (DomainModel:LegacyWrites off), when the persistence is the model-backed one. Reads
/// serve what it returns, which is already the model's projection. A document already in hand (a write handler's
/// result, or GetById's after its backfill) is read back by id instead: a write's working copy carries the legacy
/// persistence's version and updatedAt, not the model's, and the response filter only runs once the write has
/// committed, so the read sees the change.
/// </summary>
public sealed class CutApplicationReader(IAccreditationApplicationPersistence persistence)
    : IApplicationReader
{
    public Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return persistence.GetByIdAsync(organisationId, applicationId);
    }

    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return [.. await persistence.GetByOrganisationAsync(organisationId)];
    }

    public Task<AccreditationApplicationModel?> ServeFromSourceAsync(
        AccreditationApplicationModel legacy,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return legacy.Id is { } id
            ? persistence.GetByIdAsync(legacy.OrganisationId, id.ToString())
            : Task.FromResult<AccreditationApplicationModel?>(null);
    }

    // Only the accreditation API serves many, and only documents it has just read from the persistence: the model's
    // projection already, so a second read would only cost one per application.
    public Task<IReadOnlyList<AccreditationApplicationModel>> ServeManyFromSourceAsync(
        IReadOnlyList<AccreditationApplicationModel> legacy,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(legacy);
    }
}
