using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Persistence;

/// <summary>
/// Typed reads and writes for <c>applicationVersions</c>, which holds every application type. Callers name the
/// details type they expect, so none of them casts <see cref="ApplicationEntity.Details"/> or filters on <c>_t</c>
/// by hand. These extend <see cref="IVersionedPersistence{T}"/>, which <see cref="ApplicationVersionPersistence"/>
/// implements and which the dual-write sync and the migration already hold.
/// </summary>
public static class ApplicationVersions
{
    private static FilterDefinitionBuilder<ApplicationEntity> Filter =>
        Builders<ApplicationEntity>.Filter;

    /// <summary>Writes version 1 of an application whose details are a <typeparamref name="TDetails"/>.</summary>
    /// <exception cref="ArgumentException">The details are another type.</exception>
    public static Task<ApplicationEntity> CreateAsync<TDetails>(
        this IVersionedPersistence<ApplicationEntity> applications,
        ApplicationEntity application,
        ChangeInfo change,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
        where TDetails : ApplicationDetails
    {
        RequireDetails<TDetails>(application);
        return applications.Versions.InsertAsync(application, change, session, cancellationToken);
    }

    /// <summary>The current version, or null when there is none or it is not a <typeparamref name="TDetails"/>.</summary>
    public static async Task<ApplicationEntity?> GetCurrentAsync<TDetails>(
        this IVersionedPersistence<ApplicationEntity> applications,
        ObjectId entityId,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
        where TDetails : ApplicationDetails
    {
        var current = await applications.Versions.GetCurrentAsync(
            entityId,
            session,
            cancellationToken
        );
        return current?.Details is TDetails ? current : null;
    }

    /// <summary>
    /// Writes the next version of a <typeparamref name="TDetails"/> application. An application never changes type:
    /// a new version of another type is refused, so it cannot slip past rules chosen by type.
    /// </summary>
    /// <exception cref="ArgumentException">The proposed details are not a <typeparamref name="TDetails"/>.</exception>
    /// <exception cref="InvalidOperationException">The current version is another type.</exception>
    /// <exception cref="VersionConflictException">The current version is not <paramref name="expectedVersion"/>.</exception>
    public static async Task<VersionedWriteResult<ApplicationEntity>> UpdateAsync<TDetails>(
        this IVersionedPersistence<ApplicationEntity> applications,
        ApplicationEntity application,
        long expectedVersion,
        ChangeInfo change,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
        where TDetails : ApplicationDetails
    {
        RequireDetails<TDetails>(application);
        // The repository's own version check makes this read safe: if the type changed after it, so did the version.
        var current = await applications.Versions.GetCurrentAsync(
            application.EntityId,
            session,
            cancellationToken
        );
        if (current is not null && current.Details is not TDetails)
        {
            throw new InvalidOperationException(
                $"Application {application.EntityId} is a {current.ApplicationType} application and cannot become a {application.ApplicationType} application."
            );
        }

        return await applications.Versions.UpdateAsync(
            application,
            expectedVersion,
            change,
            session,
            cancellationToken
        );
    }

    /// <summary>The current version of every application on a registration, optionally of one type only.</summary>
    public static Task<List<ApplicationEntity>> ListCurrentAsync(
        this IVersionedPersistence<ApplicationEntity> applications,
        ObjectId registrationId,
        ApplicationType? type = null,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
    {
        var filter = Filter.Eq(a => a.RegistrationId, registrationId);
        if (type is { } only)
        {
            filter &= Filter.Eq(a => a.ApplicationType, only);
        }

        return applications.Versions.FindAllCurrentAsync(filter, session, cancellationToken);
    }

    /// <summary>
    /// The ORSs, and the interim sites on them, that the registration's open applications other than
    /// <paramref name="excludingApplicationId"/> hold, whatever their type. The same query serves both directions:
    /// a reapplication asks it about in-year ORS changes, and an in-year ORS change asks it about reapplications.
    /// </summary>
    public static async Task<SiteHoldings> SitesHeldByOtherOpenApplicationsAsync(
        this IVersionedPersistence<ApplicationEntity> applications,
        ObjectId registrationId,
        ObjectId excludingApplicationId,
        IClientSessionHandle? session = null,
        CancellationToken cancellationToken = default
    )
    {
        var others = await applications.Versions.FindAllCurrentAsync(
            Filter.Eq(a => a.RegistrationId, registrationId)
                & Filter.Ne(a => a.EntityId, excludingApplicationId)
                & Filter.Nin(a => a.Status, ApplicationStatusExtensions.Closed),
            session,
            cancellationToken
        );
        return SiteHoldings.Of(others.SelectMany(a => a.Details.HeldSites()));
    }

    private static void RequireDetails<TDetails>(ApplicationEntity application)
        where TDetails : ApplicationDetails
    {
        if (application.Details is not TDetails)
        {
            throw new ArgumentException(
                $"Application {application.EntityId} has {application.ApplicationType} details, not {typeof(TDetails).Name}.",
                nameof(application)
            );
        }
    }
}

/// <summary>ORSs (by <c>entityId</c>) and interim sites (ORS and site number) that open applications hold.</summary>
public sealed record SiteHoldings(
    HashSet<ObjectId> Sites,
    HashSet<(ObjectId OrsEntityId, string SiteNumber)> Interims
)
{
    /// <summary>What a set of application site entries holds. An entry not yet linked to an ORS holds nothing.</summary>
    public static SiteHoldings Of(IEnumerable<ApplicationOverseasSiteEntry> entries)
    {
        var held = entries.Where(e => e.OverseasSiteId is not null).ToList();
        return new SiteHoldings(
            held.Select(e => e.OverseasSiteId!.Value).ToHashSet(),
            held.SelectMany(e =>
                    e.InterimSites.Select(i => (e.OverseasSiteId!.Value, i.SiteNumber))
                )
                .ToHashSet()
        );
    }
}
