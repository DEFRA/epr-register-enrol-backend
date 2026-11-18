using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// epr-2uxy AC02/AC03: sets isNewSite = false on the overseas sites IsNewSiteClassifier finds
// PROVABLY-CORRUPT or PROMOTED-CORRECTABLE, and on nothing else. Run it BEFORE management-be's
// ReAccreditationIsNewSiteCorrectionMigration: a query-resubmit sends this service's overseas sites
// and management-be replaces payload.overseasSites.sites[] with them, so an uncorrected true here
// undoes the management-be correction (docs/diagnostics/epr-2uxy-isnewsite-correction.md).
//
// Gates, as management-be's:
//   1. Diagnostics:Ra292CorrectIsNewSite        off (absent) -> does nothing at all.
//   2. Diagnostics:Ra292SpotCheckConfirmedBy    must name whoever spot-checked the audit output.
//   3. Diagnostics:Ra292CorrectIsNewSiteApply   absent/false -> dry run: logs, writes nothing.
//   4. per-site verdict                         AMBIGUOUS-REFUSED is refused in code, naming why.
// Plus Diagnostics:Ra292DeployedAt, required. A missing or malformed value refuses the run and logs
// at Error.
//
// Idempotent: a corrected site reads false and classifies NOT-FLAGGED-NEW on any later run.
//
// Each application is written through IDualWriteUnitOfWork, like every operator write path. With
// DomainModel:DualWrite on, the unit of work first ensures the application is migrated, and after
// the write re-syncs it, which re-derives the Application version's
// applicationOverseasSites[].isNewSite from the corrected legacy site. The ORS entity is not
// re-derived: Origin, Status and ProposedByApplicationId are fixed when the ORS is created from
// isNewSite and only ever carried over (LegacySiteMapper.WithDetailsOf). A site with no OrsId gets
// an ORS of its own (ApplicationGroupMigrator), which a wrongly-defaulted true made an Operator
// proposal of this application; it is set here to what the migrator makes of a ReEx site (ReEx,
// Active, no proposer), in the same transaction. With DualWrite off there is no transaction and
// nothing in the domain model is written: the migration (which needs DualWrite) has not run, and
// when it does it reads the corrected legacy document.
//
// The write is a targeted $set of the site list guarded on the Version read inside the transaction,
// so a concurrent operator save is never overwritten; and it bumps Version, so a stale
// whole-document replace cannot write the old true back.
public sealed class IsNewSiteCorrectionMigration(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<IsNewSiteCorrectionMigration> logger
) : BackgroundService
{
    public const string Operation = "Epr2uxyIsNewSiteCorrection";

    internal enum RunResult
    {
        Disabled,
        Refused,
        DryRun,
        Applied,
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Each application is its own guarded write, so a failure part-way leaves a state
            // the next run re-plans from.
            logger.LogError(ex, "epr-2uxy isNewSite correction failed");
        }
    }

    internal async Task<RunResult> RunAsync(CancellationToken cancellationToken)
    {
        var enabled = IsNewSiteConfig.ReadFlag(configuration, IsNewSiteConfig.CorrectKey, logger);
        if (enabled is null)
            return RunResult.Refused;
        if (enabled == false)
            return RunResult.Disabled;

        var confirmedBy = configuration[IsNewSiteConfig.SpotCheckConfirmedByKey];
        if (string.IsNullOrWhiteSpace(confirmedBy))
        {
            logger.LogError(
                "epr-2uxy isNewSite correction is enabled but {SpotCheckKey} is not set, so it "
                    + "will not run. Set it to whoever spot-checked the audit output against the "
                    + "operator database",
                IsNewSiteConfig.SpotCheckConfirmedByKey
            );
            return RunResult.Refused;
        }

        var apply = IsNewSiteConfig.ReadFlag(configuration, IsNewSiteConfig.ApplyKey, logger);
        if (apply is null)
            return RunResult.Refused;
        if (IsNewSiteConfig.ReadDeployedAt(configuration, logger) is not { } deployedAt)
            return RunResult.Refused;

        var mode = apply == true ? "APPLY" : "DRY RUN (nothing will be written)";
        logger.LogInformation(
            "epr-2uxy isNewSite correction starting. Mode={Mode}. Spot check confirmed by "
                + "{ConfirmedBy}. Applications created before {DeployedAt:o}",
            mode,
            confirmedBy,
            deployedAt
        );

        var persistence = serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>();
        var scans = await new IsNewSiteScanner(
            persistence,
            serviceProvider.GetRequiredService<IReExApiAdapter>(),
            logger
        ).ScanAsync(deployedAt, cancellationToken);

        int sitesCorrected = 0,
            sitesRefused = 0,
            applicationsChanged = 0;
        foreach (var scan in scans)
        {
            foreach (
                var refused in scan.Sites.Where(s =>
                    s.Classification.Verdict == IsNewSiteVerdict.AmbiguousRefused
                )
            )
            {
                sitesRefused++;
                logger.LogWarning(
                    "epr-2uxy REFUSED to correct application {ApplicationId} site {SiteId} "
                        + "'{SiteName}': no OrsId and no proof it came from ReEx, so it may be a "
                        + "genuinely new site. refusedBecause: {RefusedBecause}",
                    scan.Id,
                    refused.SiteId,
                    refused.SiteName,
                    string.Join(", ", refused.Classification.RefusedBecause)
                );
            }

            var planned = scan.Correctable.ToList();
            if (planned.Count == 0)
                continue;

            var siteList = Describe(planned);
            if (apply != true)
            {
                logger.LogInformation(
                    "epr-2uxy DRY RUN would correct application {ApplicationId} "
                        + "ref={ApplicationReference}: {Sites}",
                    scan.Id,
                    scan.ApplicationReference,
                    siteList
                );
                sitesCorrected += planned.Count;
                applicationsChanged++;
                continue;
            }

            if (await CorrectAsync(persistence, scan, planned, cancellationToken))
            {
                sitesCorrected += planned.Count;
                applicationsChanged++;
                logger.LogInformation(
                    "epr-2uxy APPLIED: application {ApplicationId} ref={ApplicationReference} "
                        + "isNewSite set to false on {Sites}. Spot check confirmed by {ConfirmedBy}",
                    scan.Id,
                    scan.ApplicationReference,
                    siteList,
                    confirmedBy
                );
            }
        }

        logger.LogInformation(
            "epr-2uxy isNewSite correction complete. Mode={Mode}. Applications {ApplicationsVerb}: "
                + "{ApplicationsChanged}. Sites {SitesVerb}: {SitesCorrected}. Sites refused as "
                + "ambiguous (adjudicate by hand): {SitesRefused}",
            mode,
            apply == true ? "corrected" : "that would be corrected",
            applicationsChanged,
            apply == true ? "corrected" : "that would be corrected",
            sitesCorrected,
            sitesRefused
        );
        return apply == true ? RunResult.Applied : RunResult.DryRun;
    }

    private async Task<bool> CorrectAsync(
        IAccreditationApplicationPersistence persistence,
        ApplicationScan scan,
        IReadOnlyList<SiteScan> planned,
        CancellationToken cancellationToken
    )
    {
        var unitOfWork = serviceProvider.GetRequiredService<IDualWriteUnitOfWork>();
        // Registered only with DomainModel:DualWrite (or DualWriteSeed) on.
        var stores = serviceProvider.GetService<ApplicationGroupStores>();
        var plannedIds = planned.Select(p => p.SiteId).ToHashSet();

        var result = await unitOfWork.RunAsync(
            Operation,
            scan.Id.ToString(),
            async (session, token) =>
            {
                // Re-read inside the transaction: the unit of work can retry this callback.
                var application = await persistence.GetByIdAsync(
                    scan.OrganisationId,
                    scan.Id.ToString(),
                    session
                );
                var sites = application?.OverseasSites?.Sites;
                if (application is null || sites is null || !StillAsPlanned(sites, planned))
                    return Results.Conflict();

                foreach (var site in sites.Where(s => plannedIds.Contains(s.SiteId)))
                    site.IsNewSite = false;

                var written = await persistence.UpdateFieldsAsync(
                    scan.Id,
                    Builders<AccreditationApplicationModel>.Filter.Eq(
                        a => a.Version,
                        application.Version
                    ),
                    Builders<AccreditationApplicationModel>.Update.Set(
                        a => a.OverseasSites!.Sites,
                        sites
                    ),
                    session,
                    token
                );
                if (written is null)
                    return Results.Conflict();

                if (session is not null && stores is not null)
                    await CorrectOverseasSiteEntitiesAsync(
                        stores,
                        scan.Id,
                        sites.Where(s => plannedIds.Contains(s.SiteId)).ToList(),
                        session,
                        token
                    );
                return Results.NoContent();
            },
            cancellationToken
        );

        if (result is Microsoft.AspNetCore.Http.HttpResults.NoContent)
            return true;

        logger.LogError(
            "epr-2uxy: application {ApplicationId} was not corrected: it changed after planning "
                + "or the write failed ({Result}). Nothing was written to it; re-run the dry run "
                + "to see its current state",
            scan.Id,
            result.GetType().Name
        );
        return false;
    }

    // Every planned site must still be flagged true with no OrsId and the name ReEx confirmed,
    // or the provenance the plan rested on no longer holds.
    private static bool StillAsPlanned(List<OverseasSiteModel> sites, IReadOnlyList<SiteScan> planned) =>
        planned.All(p =>
            sites.Count(s => s.SiteId == p.SiteId) == 1
            && sites.Single(s => s.SiteId == p.SiteId) is var site
            && site.IsNewSite
            && string.IsNullOrWhiteSpace(site.OrsId)
            && site.SiteName == p.SiteName
        );

    private static async Task CorrectOverseasSiteEntitiesAsync(
        ApplicationGroupStores stores,
        ObjectId applicationId,
        List<OverseasSiteModel> corrected,
        IClientSessionHandle session,
        CancellationToken cancellationToken
    )
    {
        var current = await stores.Applications.Versions.FindCurrentAsync(
            Builders<ApplicationEntity>.Filter.Eq(a => a.EntityId, applicationId),
            session,
            cancellationToken
        );
        if (current is null)
            return;

        var entries = current.AsAccreditation().ApplicationOverseasSites;
        foreach (var site in corrected)
        {
            if (entries.Find(e => e.SiteId == site.SiteId)?.OverseasSiteId is not { } orsEntityId)
                continue;
            var ors = await stores.OverseasSites.Versions.FindCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(o => o.EntityId, orsEntityId),
                session,
                cancellationToken
            );
            // Only this application's own OrsId-less proposal, which is what the wrong true made.
            if (
                ors is null
                || ors.OrsId is not null
                || ors.Origin != SiteOrigin.Operator
                || ors.ProposedByApplicationId != applicationId
            )
                continue;

            var next = LegacySiteMapper.WithDetailsOf(ors, site);
            next.Origin = SiteOrigin.ReEx;
            next.Status = OverseasSiteStatus.Active;
            next.ProposedByApplicationId = null;
            await stores.OverseasSites.Versions.UpdateAsync(
                next,
                ors.Version,
                new ChangeInfo
                {
                    Reason = ChangeReason.Migration,
                    ApplicationId = applicationId,
                    ChangedBy = "epr-2uxy isNewSite correction",
                },
                session,
                cancellationToken
            );
        }
    }

    private static string Describe(IEnumerable<SiteScan> sites) =>
        string.Join(
            ", ",
            sites.Select(s =>
                $"{s.SiteId} '{s.SiteName}' ({IsNewSiteClassifier.BucketName(s.Classification.Verdict)})"
            )
        );
}
