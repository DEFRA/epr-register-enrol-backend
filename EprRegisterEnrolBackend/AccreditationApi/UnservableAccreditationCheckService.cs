using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.AccreditationApi;

public class AccreditationApiMonitoringConfig
{
    public const string SectionName = "AccreditationApiMonitoring";

    /// <summary>Switches the check off, for a host that must not query the database.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long after start-up the first check waits, so it never delays start-up.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan Interval { get; set; } = TimeSpan.FromDays(1);
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
    public async Task RunOnceAsync()
    {
        var firstYear = clients.Value.FirstAccreditationYear;
        try
        {
            var counts = await persistence.CountUnservableApprovedAsync(firstYear);
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

        try
        {
            await Task.Delay(settings.InitialDelay, stoppingToken);
            using var timer = new PeriodicTimer(settings.Interval);
            do
            {
                await RunOnceAsync();
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
    }
}
