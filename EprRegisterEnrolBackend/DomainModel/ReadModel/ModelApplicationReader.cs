using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>
/// RA-705: serves the domain model's projection in the legacy shape, except Version, CreatedAt and UpdatedAt,
/// which are copied from the legacy document. An application missing from the model is null (404),
/// with no fallback to legacy.
/// </summary>
public sealed class ModelApplicationReader(
    IMongoDbClientFactory clientFactory,
    ApplicationReadModel readModel,
    IAccreditationApplicationPersistence applications
) : IApplicationReader
{
    /// <summary>Gets a single application by ID, served from the model with legacy metadata.</summary>
    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId,
        CancellationToken cancellationToken = default
    )
    {
        var legacy = await applications.GetByIdAsync(organisationId, applicationId);
        if (legacy is null)
        {
            return null;
        }

        return await ServeFromSourceAsync(legacy, cancellationToken);
    }

    /// <summary>Gets all applications for an organisation, served from the model with legacy metadata.</summary>
    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        return await ServeManyFromSourceAsync(
            [.. await applications.GetByOrganisationAsync(organisationId)],
            cancellationToken
        );
    }

    /// <summary>Transforms a legacy application into the model's projection while preserving legacy metadata.</summary>
    public async Task<AccreditationApplicationModel?> ServeFromSourceAsync(
        AccreditationApplicationModel legacy,
        CancellationToken cancellationToken = default
    )
    {
        using var session = await clientFactory
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);
        return await ServeFromSourceAsync(legacy, session, cancellationToken);
    }

    /// <summary>
    /// The configured source's version of each legacy document already read, in the same order; those the
    /// source doesn't hold are left out.
    /// </summary>
    public async Task<IReadOnlyList<AccreditationApplicationModel>> ServeManyFromSourceAsync(
        IReadOnlyList<AccreditationApplicationModel> legacy,
        CancellationToken cancellationToken = default
    )
    {
        using var session = await clientFactory
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);

        var results = new List<AccreditationApplicationModel>();
        foreach (var item in legacy)
        {
            var served = await ServeFromSourceAsync(item, session, cancellationToken);
            if (served is not null)
            {
                results.Add(served);
            }
        }

        return results;
    }

    private async Task<AccreditationApplicationModel?> ServeFromSourceAsync(
        AccreditationApplicationModel legacy,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        if (legacy.Id is null)
        {
            return null;
        }

        var projection = await readModel.GetAsync(legacy.Id.Value, session, cancellationToken);
        if (projection is null)
        {
            return null;
        }

        projection.Version = legacy.Version;
        projection.CreatedAt = legacy.CreatedAt;
        projection.UpdatedAt = legacy.UpdatedAt;

        return projection;
    }
}
