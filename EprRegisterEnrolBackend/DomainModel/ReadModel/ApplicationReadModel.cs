using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.ReadModel;

/// <summary>Everything one application's legacy shape is built from.</summary>
public sealed record ApplicationSources(
    ApplicationEntity Application,
    OrganisationEntity? Organisation,
    RegistrationEntity? Registration,
    IReadOnlyDictionary<ObjectId, OverseasSiteEntity> OverseasSites,
    AccreditationEntity? Accreditation
);

/// <summary>
/// Reads an application from the domain model in the legacy shape (design doc phase 5). Nothing serves it yet, only
/// reconciliation uses it, so the routes can later switch to exactly what has been checked.
/// </summary>
public sealed class ApplicationReadModel(ApplicationGroupStores stores)
{
    public async Task<AccreditationApplicationModel?> GetAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    ) =>
        await LoadAsync(applicationId, session, cancellationToken) is { } sources
            ? ApplicationProjection.ToLegacy(sources)
            : null;

    /// <summary>The application's sources, or null when it has no current accreditation application version.</summary>
    public async Task<ApplicationSources?> LoadAsync(
        ObjectId applicationId,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default
    )
    {
        var application = await stores.Applications.Versions.FindCurrentAsync(
            Builders<ApplicationEntity>.Filter.Eq(a => a.EntityId, applicationId),
            session,
            cancellationToken
        );
        if (application?.Details is not AccreditationDetails details)
        {
            return null;
        }

        var siteIds = details
            .ApplicationOverseasSites.Select(s => s.OverseasSiteId)
            .OfType<ObjectId>()
            .Distinct()
            .ToList();
        var sites = await stores.OverseasSites.Versions.FindAllCurrentAsync(
            Builders<OverseasSiteEntity>.Filter.In(s => s.EntityId, siteIds),
            session,
            cancellationToken
        );

        return new ApplicationSources(
            application,
            await stores.Organisations.FindByReExOrganisationIdAsync(
                application.ReExOrganisationId,
                session,
                cancellationToken
            ),
            application.RegistrationId is { } registrationId
                ? await stores.Registrations.Versions.FindCurrentAsync(
                    Builders<RegistrationEntity>.Filter.Eq(r => r.EntityId, registrationId),
                    session,
                    cancellationToken
                )
                : null,
            sites.ToDictionary(s => s.EntityId),
            details.AccreditationId is { } accreditationId
                ? await stores.Accreditations.Versions.FindCurrentAsync(
                    Builders<AccreditationEntity>.Filter.Eq(a => a.EntityId, accreditationId),
                    session,
                    cancellationToken
                )
                : null
        );
    }
}
