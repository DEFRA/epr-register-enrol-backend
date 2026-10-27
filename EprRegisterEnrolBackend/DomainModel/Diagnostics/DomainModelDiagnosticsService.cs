using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.DomainModel.Diagnostics;

public class DomainModelDiagnosticsConfig
{
    public const string SectionName = "DomainModelDiagnostics";

    /// <summary>
    /// Off by default and switched on only in test environments. Production data cannot be examined by this
    /// team, so these checks are only meaningful, and only wanted, where the data is test data (R15).
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Runs the B0 transaction smoke test and the B1 legacy-data survey once, at startup, when
/// <see cref="DomainModelDiagnosticsConfig.Enabled"/> is set, and logs one line for each. CDP gives no shell, so
/// logs are how a test environment reports back. Both read or write only throwaway data, never log personal data,
/// and a failure is logged as an error and does not stop the host.
/// </summary>
public class DomainModelDiagnosticsService(
    IMongoDbClientFactory mongo,
    IOptions<DomainModelDiagnosticsConfig> config,
    ILogger<DomainModelDiagnosticsService> logger
) : BackgroundService
{
    private const string LegacyCollection = "accreditationApplications";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Yield so host startup is never held up by anything below.
        await Task.Yield();
        await RunAsync(stoppingToken);
    }

    // Extracted so tests can await it deterministically.
    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!config.Value.Enabled)
        {
            return;
        }

        try
        {
            var database = mongo.GetCollection<BsonDocument>(LegacyCollection).Database;

            var smoke = await TransactionSmokeTest.RunAsync(database, cancellationToken);
            if (smoke.Supported)
            {
                logger.LogInformation("DomainModelDiagnostics transactions supported=true");
            }
            else
            {
                logger.LogError(
                    "DomainModelDiagnostics transactions supported=false commit={Commit} abort={Abort} error={ErrorKind}",
                    smoke.CommitWorks,
                    smoke.AbortWorks,
                    smoke.ErrorKind
                );
            }

            var report = await LegacyDataSurvey.RunAsync(
                database.GetCollection<BsonDocument>(LegacyCollection),
                cancellationToken
            );
            logger.LogInformation("DomainModelDiagnostics survey {Report}", report.ToLogLine());
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down.
        }
        catch (Exception ex)
        {
            logger.LogError("DomainModelDiagnostics failed error={ErrorKind}", ex.GetType().Name);
        }
    }
}
