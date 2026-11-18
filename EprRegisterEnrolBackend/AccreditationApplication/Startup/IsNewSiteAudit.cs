using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// epr-2uxy AC01: READ-ONLY diagnostic. Classifies every overseas site on the applications created
// before the RA-292 deploy (see IsNewSiteClassifier) and logs, per bucket, the count and the
// application ids. Writes nothing.
//
// Off unless Diagnostics:Ra292IsNewSiteAudit is true; then Diagnostics:Ra292DeployedAt is required.
// Enable, boot, read the log, turn it off: docs/diagnostics/epr-2uxy-isnewsite-correction.md.
//
// A BackgroundService like ChargeCorrectionMigration, so a Mongo or ReEx failure logs rather than
// crash-looping the host. Nothing is resolved until it is enabled.
public sealed class IsNewSiteAudit(
    IServiceProvider serviceProvider,
    IConfiguration configuration,
    ILogger<IsNewSiteAudit> logger
) : BackgroundService
{
    internal sealed record Result(
        int ApplicationsScanned,
        IReadOnlyDictionary<IsNewSiteVerdict, IReadOnlyList<string>> ApplicationIdsByBucket,
        IReadOnlyDictionary<IsNewSiteVerdict, int> SiteCounts
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "epr-2uxy isNewSite audit failed; nothing was written");
        }
    }

    // Null when disabled or misconfigured.
    internal async Task<Result?> RunAsync(CancellationToken cancellationToken)
    {
        if (IsNewSiteConfig.ReadFlag(configuration, IsNewSiteConfig.AuditKey, logger) != true)
            return null;
        if (IsNewSiteConfig.ReadDeployedAt(configuration, logger) is not { } deployedAt)
            return null;

        var scanner = new IsNewSiteScanner(
            serviceProvider.GetRequiredService<IAccreditationApplicationPersistence>(),
            serviceProvider.GetRequiredService<IReExApiAdapter>(),
            logger
        );
        var scans = await scanner.ScanAsync(deployedAt, cancellationToken);

        var verdicts = Enum.GetValues<IsNewSiteVerdict>();
        var siteCounts = verdicts.ToDictionary(
            v => v,
            v => scans.Sum(a => a.Sites.Count(s => s.Classification.Verdict == v))
        );
        var idsByBucket = verdicts.ToDictionary(
            v => v,
            v =>
                (IReadOnlyList<string>)
                    [
                        .. scans
                            .Where(a => a.Sites.Any(s => s.Classification.Verdict == v))
                            .Select(a => a.Id.ToString()),
                    ]
        );
        var correctable =
            siteCounts[IsNewSiteVerdict.ProvablyCorrupt]
            + siteCounts[IsNewSiteVerdict.PromotedCorrectable];

        // Each placeholder name appears once: a repeated name is a separate positional slot to
        // the structured-logging formatter.
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
            "epr-2uxy isNewSite audit (READ-ONLY, nothing written). Applications created before "
                + "{DeployedAt:o} with overseas sites: {ApplicationsScanned}. Sites per bucket - "
                + "PROVABLY-CORRUPT: {SitesProvablyCorrupt}; PROMOTED-CORRECTABLE: "
                + "{SitesPromotedCorrectable}; AMBIGUOUS-REFUSED: {SitesAmbiguousRefused}; "
                + "ALREADY-CORRECT: {SitesAlreadyCorrect}; not flagged new: {SitesNotFlaggedNew}. "
                + "Total correctable: {SitesCorrectable}",
            deployedAt,
            scans.Count,
            siteCounts[IsNewSiteVerdict.ProvablyCorrupt],
            siteCounts[IsNewSiteVerdict.PromotedCorrectable],
            siteCounts[IsNewSiteVerdict.AmbiguousRefused],
            siteCounts[IsNewSiteVerdict.AlreadyCorrect],
            siteCounts[IsNewSiteVerdict.NotFlaggedNew],
            correctable
        );

        foreach (
            var verdict in new[]
            {
                IsNewSiteVerdict.ProvablyCorrupt,
                IsNewSiteVerdict.PromotedCorrectable,
                IsNewSiteVerdict.AmbiguousRefused,
                IsNewSiteVerdict.AlreadyCorrect,
            }
        )
        {
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation(
                "epr-2uxy isNewSite audit {Bucket}: {SiteCount} sites on {ApplicationCount} "
                    + "applications: {ApplicationIds}",
                IsNewSiteClassifier.BucketName(verdict),
                siteCounts[verdict],
                idsByBucket[verdict].Count,
                string.Join(", ", idsByBucket[verdict])
            );
        }

        if (siteCounts[IsNewSiteVerdict.AmbiguousRefused] > correctable)
            logger.LogWarning(
                "epr-2uxy isNewSite audit: more sites were refused ({SitesAmbiguousRefused}) than "
                    + "found correctable ({SitesCorrectable}). Question the classifier before "
                    + "concluding the data cannot be corrected: if the same refusedBecause reason "
                    + "recurs across many sites, the check is miscalibrated rather than the data "
                    + "messy",
                siteCounts[IsNewSiteVerdict.AmbiguousRefused],
                correctable
            );

        foreach (var scan in scans)
        {
            foreach (var site in scan.Sites.Where(s => s.Classification.IsCorrectable))
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                    "epr-2uxy {Bucket} application {ApplicationId} ref={ApplicationReference} "
                        + "site {SiteId} '{SiteName}'",
                    IsNewSiteClassifier.BucketName(site.Classification.Verdict),
                    scan.Id,
                    scan.ApplicationReference,
                    site.SiteId,
                    site.SiteName
                );
            foreach (
                var site in scan.Sites.Where(s =>
                    s.Classification.Verdict == IsNewSiteVerdict.AmbiguousRefused
                )
            )
                logger.LogWarning(
                    "epr-2uxy AMBIGUOUS-REFUSED application {ApplicationId} "
                        + "ref={ApplicationReference} site {SiteId} '{SiteName}': no OrsId and no "
                        + "proof it came from ReEx. Adjudicate by hand; never auto-corrected. "
                        + "refusedBecause: {RefusedBecause}",
                    scan.Id,
                    scan.ApplicationReference,
                    site.SiteId,
                    site.SiteName,
                    string.Join(", ", site.Classification.RefusedBecause)
                );
        }

        return new Result(scans.Count, idsByBucket, siteCounts);
    }
}