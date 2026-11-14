using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>
/// Seam for accreditation application reads; switches between legacy persistence and the new domain model
/// projection based on the <c>DomainModel:ReadFromModel</c> flag.
/// </summary>
public interface IApplicationReader
{
    /// <summary>Gets an application by ID; returns null if not found or belongs to another organisation.</summary>
    Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Gets all applications for an organisation, newest first by CreatedAt, then Id.</summary>
    Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    );

    /// <summary>Returns the response for a legacy document already read; null if the source doesn't hold it.</summary>
    Task<AccreditationApplicationModel?> ServeFromSourceAsync(
        AccreditationApplicationModel legacy,
        CancellationToken cancellationToken = default
    );
}
