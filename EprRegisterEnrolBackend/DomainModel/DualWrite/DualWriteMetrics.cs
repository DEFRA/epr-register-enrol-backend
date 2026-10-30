using System.Diagnostics.Metrics;
using EprRegisterEnrolBackend.DomainModel.Seeding;

namespace EprRegisterEnrolBackend.DomainModel.DualWrite;

/// <summary>Counts dual-write failures by operation, alongside the Error log line each one writes.</summary>
public sealed class DualWriteMetrics
{
    public const string MeterName = SeedDualWriteMetrics.MeterName;
    public const string FailuresInstrument = "domain_model.dual_write.failures";
    public const string OperationTag = "operation";

    private readonly Counter<long> _failures;

    public DualWriteMetrics(IMeterFactory meterFactory)
    {
        _failures = meterFactory
            .Create(MeterName)
            .CreateCounter<long>(
                FailuresInstrument,
                description: "Dual-writes that failed, rolled back their legacy write and returned 500."
            );
    }

    public void RecordFailure(string operation) =>
        _failures.Add(1, new KeyValuePair<string, object?>(OperationTag, operation));
}
