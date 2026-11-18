using System.Globalization;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.AccreditationApplication.Startup;

// RA-794: the configuration both the audit and the correction read. The keys match
// management-be's, so both services are run the same way (see
// docs/diagnostics/ra-794-isnewsite-correction.md).
public static class IsNewSiteConfig
{
    public const string AuditKey = "Diagnostics:Ra292IsNewSiteAudit";
    public const string DeployedAtKey = "Diagnostics:Ra292DeployedAt";
    public const string CorrectKey = "Diagnostics:Ra292CorrectIsNewSite";
    public const string SpotCheckConfirmedByKey = "Diagnostics:Ra292SpotCheckConfirmedBy";
    public const string ApplyKey = "Diagnostics:Ra292CorrectIsNewSiteApply";

    // An on/off gate. Absent means off: both management-be's runbook and this one document every
    // gate as "off by default - a no-op in every environment until deliberately enabled", and
    // ApplyKey absent as "dry run unless explicitly true". Present but not a boolean is a
    // misconfiguration: logged at Error and reported as unusable (null), so the caller refuses.
    public static bool? ReadFlag(IConfiguration configuration, string key, ILogger logger)
    {
        var raw = configuration[key];
        if (raw is null)
            return false;
        if (bool.TryParse(raw.Trim(), out var value))
            return value;

        logger.LogError(
            "RA-794: {ConfigKey} is set but is not true or false, so nothing will run",
            key
        );
        return null;
    }

    // Required whenever the audit or the correction runs; no default. The applications that can
    // hold a wrongly-defaulted isNewSite are the ones created before the RA-292 fix was deployed.
    public static DateTime? ReadDeployedAt(IConfiguration configuration, ILogger logger)
    {
        var raw = configuration[DeployedAtKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            logger.LogError(
                "RA-794: {ConfigKey} is not set, so nothing will run. Set it to the time RA-292 "
                    + "was deployed to this environment (ISO 8601, UTC)",
                DeployedAtKey
            );
            return null;
        }

        if (
            DateTime.TryParse(
                raw.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var deployedAt
            )
        )
            return deployedAt;

        logger.LogError(
            "RA-794: {ConfigKey} is not a date and time, so nothing will run",
            DeployedAtKey
        );
        return null;
    }
}

public sealed record SiteScan(int SiteId, string SiteName, IsNewSiteClassification Classification);

public sealed record ApplicationScan(
    ObjectId Id,
    string OrganisationId,
    string? ApplicationReference,
    IReadOnlyList<SiteScan> Sites
)
{
    public IEnumerable<SiteScan> Correctable => Sites.Where(s => s.Classification.IsCorrectable);
}

// RA-794: reads the applications created before the RA-292 deploy and classifies every overseas
// site on them. Read-only: it calls no write method on the persistence, and the only ReEx call is
// the read the seed made, for an application holding a flagged site with no OrsId.
public sealed class IsNewSiteScanner(
    IAccreditationApplicationPersistence persistence,
    IReExApiAdapter reEx,
    ILogger logger
)
{
    public async Task<IReadOnlyList<ApplicationScan>> ScanAsync(
        DateTime createdBefore,
        CancellationToken cancellationToken
    )
    {
        var applications = await persistence.GetCreatedBeforeWithOverseasSitesAsync(
            createdBefore,
            cancellationToken
        );
        var scans = new List<ApplicationScan>(applications.Count);
        foreach (var application in applications)
            scans.Add(await ScanAsync(application));
        return scans;
    }

    public async Task<ApplicationScan> ScanAsync(AccreditationApplicationModel application)
    {
        var sites = application.OverseasSites?.Sites ?? [];
        var lookup = sites.Any(IsNewSiteClassifier.NeedsReEx)
            ? await LookUpReExAsync(application)
            : null;
        return new ApplicationScan(
            application.Id!.Value,
            application.OrganisationId,
            application.ApplicationReference,
            [
                .. sites.Select(s => new SiteScan(
                    s.SiteId,
                    s.SiteName,
                    IsNewSiteClassifier.Classify(s, lookup)
                )),
            ]
        );
    }

    // The seed's own call (AccreditationApplicationEndpoints: GetAccreditationAsync with the
    // previous year). A failure refuses this application's unproven sites, never corrects them.
    private async Task<ReExSiteLookup> LookUpReExAsync(AccreditationApplicationModel application)
    {
        var missing = string.IsNullOrWhiteSpace(application.OrganisationId) ? "organisationId"
            : string.IsNullOrWhiteSpace(application.RegistrationId) ? "registrationId"
            : null;
        if (missing is not null)
        {
            logger.LogError(
                "RA-794: application {ApplicationId} has no ReEx {MissingValue}, so its overseas "
                    + "sites cannot be checked against ReEx and will be refused",
                application.Id,
                missing
            );
            return ReExSiteLookup.Failed($"no {missing} on the application");
        }

        var result = await reEx.GetAccreditationAsync(
            application.OrganisationId,
            application.RegistrationId,
            application.MaterialType,
            application.Year - 1
        );
        if (result.IsSuccess && result.Value is { } accreditation)
            return ReExSiteLookup.Found(accreditation.OverseasSites);

        logger.LogError(
            "RA-794: ReEx did not return the overseas sites for application {ApplicationId} "
                + "(status {StatusCode}), so its unproven sites will be refused",
            application.Id,
            result.StatusCode
        );
        return ReExSiteLookup.Failed($"status {result.StatusCode?.ToString() ?? "none"}");
    }
}
