using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApi;

public class AccreditationApiMonitoringConfig
{
    public const string SectionName = "AccreditationApiMonitoring";

    /// <summary>Switches the check off, for a host that must not query the database.</summary>
    public bool Enabled { get; set; } = true;

    public static readonly TimeSpan DefaultInitialDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// How long after start-up the first check waits, so it never delays start-up. From zero to one
    /// day; anything else is ignored with a warning and the default is used.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = DefaultInitialDelay;

    /// <summary>
    /// How often the check runs. Above zero and at most 30 days; anything else is ignored with a
    /// warning and the default is used.
    /// </summary>
    public TimeSpan Interval { get; set; } = DefaultInterval;
}

/// <summary>
/// RA-651: finds approved applications the accreditation API cannot serve because they have no
/// organisation, registration or accreditation number. The API leaves them out of every response
/// without a log line, so without this check a consumer would simply never see them. Production is
/// watched through logs and alerts, and a record that does not fit the contract is a real problem,
/// so any finding is logged at Error, where the existing "5xx and Errors" alert picks it up. It logs
/// counts only, never anything about an application. A host runs it once after start-up and then
/// every <see cref="AccreditationApiMonitoringConfig.Interval"/>.
/// </summary>
public sealed class UnservableAccreditationCheckService(
    IAccreditationApplicationPersistence persistence,
    IOptions<ServiceClientsConfig> clients,
    IOptions<AccreditationApiMonitoringConfig> monitoring,
    ILogger<UnservableAccreditationCheckService> logger
) : BackgroundService
{
    private static readonly TimeSpan MaxInitialDelay = TimeSpan.FromDays(1);
    private static readonly TimeSpan MaxInterval = TimeSpan.FromDays(30);

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var firstYear = clients.Value.FirstAccreditationYear;
        try
        {
            var counts = await persistence.CountUnservableApprovedAsync(firstYear, cancellationToken);
            if (!counts.Any)
            {
                logger.LogInformation(
                    "Every approved accreditation from {FromYear} can be served by the accreditation API",
                    firstYear
                );
                return;
            }

            logger.LogError(
                "Approved accreditations the accreditation API cannot serve: noOrganisationNumber={NoOrganisationNumber} noRegistrationNumber={NoRegistrationNumber} noAccreditationNumber={NoAccreditationNumber} fromYear={FromYear}",
                counts.NoOrganisationNumber,
                counts.NoRegistrationNumber,
                counts.NoAccreditationNumber,
                firstYear
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down: a count cut short is not a failure to report.
        }
        catch (Exception ex)
        {
            // Not an Error: failing to check is not itself a data problem, and the next run retries.
            logger.LogWarning(
                ex,
                "Checking for approved accreditations the accreditation API cannot serve failed"
            );
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = monitoring.Value;
        if (!settings.Enabled)
            return;

        // A mistyped setting must not stop the host: a negative or huge delay makes Task.Delay
        // throw, and a zero, negative or huge interval makes PeriodicTimer throw, and an exception
        // out of a hosted service stops the whole backend. So a bad value is replaced, with a warning.
        var initialDelay = Valid(
            settings.InitialDelay,
            settings.InitialDelay >= TimeSpan.Zero && settings.InitialDelay <= MaxInitialDelay,
            nameof(settings.InitialDelay),
            AccreditationApiMonitoringConfig.DefaultInitialDelay
        );
        var interval = Valid(
            settings.Interval,
            settings.Interval > TimeSpan.Zero && settings.Interval <= MaxInterval,
            nameof(settings.Interval),
            AccreditationApiMonitoringConfig.DefaultInterval
        );

        try
        {
            await Task.Delay(initialDelay, stoppingToken);
            using var timer = new PeriodicTimer(interval);
            do
            {
                await RunOnceAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }

    private TimeSpan Valid(TimeSpan value, bool isValid, string setting, TimeSpan fallback)
    {
        if (isValid)
            return value;

        logger.LogWarning(
            "Ignoring AccreditationApiMonitoring:{Setting} {Value}, which is out of range; using {Default}",
            setting,
            value,
            fallback
        );
        return fallback;
    }
}
