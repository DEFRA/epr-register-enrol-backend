using System.Diagnostics.Metrics;

namespace EprRegisterEnrolBackend.DomainModel.Seeding;

/// <summary>Counts seed dual-write failures, alongside the Error log line each one writes.</summary>
public sealed class SeedDualWriteMetrics
{
    public const string MeterName = "EprRegisterEnrolBackend.DomainModel";
    public const string FailuresInstrument = "domain_model.seed_dual_write.failures";

    private readonly Counter<long> _failures;

    public SeedDualWriteMetrics(IMeterFactory meterFactory)
    {
        _failures = meterFactory
            .Create(MeterName)
            .CreateCounter<long>(
                FailuresInstrument,
                description: "Seed dual-writes that failed and were rolled back."
            );
    }

    public void RecordFailure() => _failures.Add(1);
}
