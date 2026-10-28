using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Seeding;

/// <summary>
/// Writes the seed's entities and the application's foreign keys in one transaction of their own, separate
/// from the legacy insert (see <see cref="ISeedDualWriter"/>). Re-seeding the same registration updates the
/// current versions, and writes no new version when nothing changed.
/// </summary>
public sealed class SeedDualWriter(
    IMongoDbClientFactory clientFactory,
    SeedModelStores stores,
    IAccreditationApplicationPersistence applications,
    SeedDualWriteMetrics metrics,
    TimeProvider clock,
    ILogger<SeedDualWriter> logger
) : ISeedDualWriter
{
    /// <summary>
    /// Attempts at the whole transaction when it loses an insert race. A concurrent seed of the same
    /// registration can commit the first version between our read and our insert; the retry reads it and
    /// updates it instead.
    /// </summary>
    internal const int MaxAttempts = 3;

    public async Task WriteAsync(SeedDualWrite seed, CancellationToken cancellationToken = default)
    {
        try
        {
            await WriteWithRetryAsync(seed, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            metrics.RecordFailure();
            // Ids and the error kind only. The exception is not logged: a driver message can quote document
            // values, and logs must hold no personal data (design doc §6.1).
            logger.LogError(
                "Seed dual-write failed and was rolled back: application {ApplicationId}, organisation {ReExOrganisationId}, registration {ReExRegistrationId}, error {ErrorKind}",
                seed.Application.Id,
                seed.ReExOrganisationId,
                seed.ReExRegistrationId,
                ErrorKind(ex)
            );
        }
    }

    private async Task WriteWithRetryAsync(SeedDualWrite seed, CancellationToken cancellationToken)
    {
        using var session = await clientFactory
            .GetClient()
            .StartSessionAsync(cancellationToken: cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // WithTransactionAsync already retries transient errors (write conflicts while the rival
                // transaction is open); a duplicate key, once the rival has committed, is retried here.
                await session.WithTransactionAsync(
                    (s, token) => WriteCoreAsync(s, seed, token),
                    cancellationToken: cancellationToken
                );
                return;
            }
            catch (MongoException ex) when (attempt < MaxAttempts && IsDuplicateKey(ex)) { }
        }
    }

    private async Task<bool> WriteCoreAsync(
        IClientSessionHandle session,
        SeedDualWrite seed,
        CancellationToken cancellationToken
    )
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var organisationId = await stores.Organisations.UpsertAsync(
            seed.ReExOrganisationId,
            organisation => SeedEntityMapper.ApplyTo(organisation, seed.ReEx),
            now,
            session,
            cancellationToken
        );
        var registration = await UpsertRegistrationAsync(
            session,
            seed,
            organisationId,
            now,
            cancellationToken
        );

        var siteIds = new Dictionary<string, ObjectId>();
        foreach (var site in seed.ReEx.OverseasSites)
        {
            // ReEx keys its sites by orsId, so it is always set; a site without one has no key to upsert on.
            if (string.IsNullOrWhiteSpace(site.OrsId) || siteIds.ContainsKey(site.OrsId))
            {
                continue;
            }

            siteIds[site.OrsId] = await UpsertOverseasSiteAsync(
                session,
                seed,
                registration.EntityId,
                site,
                now,
                cancellationToken
            );
        }

        await applications.StampDomainModelForeignKeysAsync(
            seed.Application.Id!.Value,
            registration.EntityId,
            siteIds,
            session,
            cancellationToken
        );
        return true;
    }

    private async Task<RegistrationEntity> UpsertRegistrationAsync(
        IClientSessionHandle session,
        SeedDualWrite seed,
        ObjectId organisationId,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var versions = stores.Registrations.Versions;
        var current = await versions.FindCurrentAsync(
            Builders<RegistrationEntity>.Filter.Eq(
                x => x.ReExRegistrationId,
                seed.ReExRegistrationId
            ),
            session,
            cancellationToken
        );
        var registration = current ?? new RegistrationEntity();
        registration.OrganisationId = organisationId;
        SeedEntityMapper.ApplyTo(registration, seed.ReEx, seed.ReExRegistrationId, seed.Material);
        registration.Source.ImportedAt = now;

        if (current is null)
        {
            return await versions.InsertAsync(
                registration,
                Change(seed, ChangeReason.ReExImport),
                session,
                cancellationToken
            );
        }

        var result = await versions.UpdateAsync(
            registration,
            current.Version,
            Change(seed, ChangeReason.ReExRefresh),
            session,
            cancellationToken
        );
        return result.Entity;
    }

    private async Task<ObjectId> UpsertOverseasSiteAsync(
        IClientSessionHandle session,
        SeedDualWrite seed,
        ObjectId registrationId,
        OverseasSiteModel site,
        DateTime now,
        CancellationToken cancellationToken
    )
    {
        var versions = stores.OverseasSites.Versions;
        var filter = Builders<OverseasSiteEntity>.Filter;
        var current = await versions.FindCurrentAsync(
            filter.Eq(x => x.RegistrationId, registrationId) & filter.Eq(x => x.OrsId, site.OrsId),
            session,
            cancellationToken
        );
        var details = new Dictionary<string, string> { ["orsId"] = site.OrsId! };

        if (current is null)
        {
            var created = await versions.InsertAsync(
                SeedEntityMapper.NewReExSite(registrationId, site),
                Change(seed, ChangeReason.ReExImport, details),
                session,
                cancellationToken
            );
            return created.EntityId;
        }

        SeedEntityMapper.ApplyTo(current, site);
        var result = await versions.UpdateAsync(
            current,
            current.Version,
            Change(seed, ChangeReason.ReExRefresh, details),
            session,
            cancellationToken
        );
        return result.Entity.EntityId;
    }

    private static ChangeInfo Change(
        SeedDualWrite seed,
        ChangeReason reason,
        Dictionary<string, string>? details = null
    ) =>
        new()
        {
            Reason = reason,
            ApplicationId = seed.Application.Id,
            Details = details,
        };

    private static bool IsDuplicateKey(MongoException ex) =>
        ex switch
        {
            MongoWriteException write => write.WriteError?.Category
                == ServerErrorCategory.DuplicateKey,
            MongoCommandException command => command.Code == 11000,
            _ => false,
        };

    private static string ErrorKind(Exception ex) =>
        ex switch
        {
            MongoWriteException write => $"{ex.GetType().Name}:{write.WriteError?.Code}",
            MongoCommandException command => $"{ex.GetType().Name}:{command.Code}",
            _ => ex.GetType().Name,
        };
}
