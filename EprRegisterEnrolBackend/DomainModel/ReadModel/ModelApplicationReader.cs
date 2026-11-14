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
        if (legacy == null)
        {
            return null;
        }

        return await ServeAsync(legacy, cancellationToken);
    }

    /// <summary>Gets all applications for an organisation, served from the model with legacy metadata.</summary>
    public async Task<IReadOnlyList<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId,
        CancellationToken cancellationToken = default
    )
    {
        var legacyList = await applications.GetByOrganisationAsync(organisationId);
        using var session = await clientFactory
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);

        var results = new List<AccreditationApplicationModel>();
        foreach (var legacy in legacyList)
        {
            var served = await ServeAsync(legacy, session, cancellationToken);
            if (served != null)
            {
                results.Add(served);
            }
        }

        return results;
    }

    /// <summary>Transforms a legacy application into the model's projection while preserving legacy metadata.</summary>
    public async Task<AccreditationApplicationModel?> ServeAsync(
        AccreditationApplicationModel legacy,
        CancellationToken cancellationToken = default
    )
    {
        if (legacy.Id == null)
        {
            return null;
        }

        using var session = await clientFactory
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);
        return await ServeAsync(legacy, session, cancellationToken);
    }

    private async Task<AccreditationApplicationModel?> ServeAsync(
        AccreditationApplicationModel legacy,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var projection = await readModel.GetAsync(legacy.Id!.Value, session, cancellationToken);
        if (projection == null)
        {
            return null;
        }

        projection.Version = legacy.Version;
        projection.CreatedAt = legacy.CreatedAt;
        projection.UpdatedAt = legacy.UpdatedAt;

        return projection;
    }
}
