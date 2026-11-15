using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>Results of the application created-at backfill process (RA-706).</summary>
public sealed record CreatedAtBackfillResult(int Filled, int LegacyMissing, int Conflicts);

/// <summary>
/// RA-706: Application versions written before the model held <c>createdAt</c> get it from the legacy
/// accreditation application, as a new version, so the model stands alone once legacy is no longer written.
/// </summary>
public sealed class ApplicationCreatedAtBackfill(
    IVersionedPersistence<ApplicationEntity> applications,
    IAccreditationApplicationPersistence legacy,
    ILogger<ApplicationCreatedAtBackfill> logger
)
{
    public async Task<CreatedAtBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var candidates = await applications.Versions.FindAllCurrentAsync(
            Builders<ApplicationEntity>.Filter.Eq(a => a.CreatedAt, null)
                & Builders<ApplicationEntity>.Filter.Eq(
                    "applicationType",
                    nameof(ApplicationType.Accreditation)
                ),
            cancellationToken: cancellationToken
        );

        int filled = 0;
        int missing = 0;
        int conflicts = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var source = await legacy.GetByIdAsync(
                candidate.ReExOrganisationId,
                candidate.EntityId.ToString()
            );

            if (source is null)
            {
                logger.LogWarning(
                    "ApplicationCreatedAtBackfill: legacy document missing for {EntityId}",
                    candidate.EntityId
                );
                missing++;
                continue;
            }

            try
            {
                candidate.CreatedAt = source.CreatedAt;
                await applications.UpdateAsync<AccreditationDetails>(
                    candidate,
                    candidate.Version,
                    new ChangeInfo { Reason = ChangeReason.CreatedAtBackfilled },
                    cancellationToken: cancellationToken
                );
                filled++;
            }
            catch (VersionConflictException)
            {
                conflicts++;
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "ApplicationCreatedAtBackfill complete. "
                    + "Filled: {Filled}, LegacyMissing: {Missing}, Conflicts: {Conflicts}",
                filled,
                missing,
                conflicts
            );
        }

        return new CreatedAtBackfillResult(filled, missing, conflicts);
    }
}

/// <summary>
/// RA-706: Background service that triggers the application created-at backfill.
/// Failure to run does not crash the backend as it is idempotent and will retry on next restart.
/// </summary>
public sealed class ApplicationCreatedAtBackfillService(
    IServiceProvider services,
    ILogger<ApplicationCreatedAtBackfillService> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var backfill = services.GetRequiredService<ApplicationCreatedAtBackfill>();
            await backfill.RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "ApplicationCreatedAtBackfillService: backfill failed");
        }
    }
}
