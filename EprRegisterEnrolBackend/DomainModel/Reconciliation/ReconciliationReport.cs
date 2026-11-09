using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Reconciliation;

/// <summary>
/// Counts, field paths and application ids only: no names, addresses, contact details or values, so a report is safe
/// to log. Phase 5 needs <see cref="WithDifferences"/>, <see cref="NotMigrated"/> and <see cref="Unreadable"/> at zero.
/// </summary>
public sealed record ReconciliationReport(
    long Applications,
    long Matching,
    long WithDifferences,
    long NotMigrated,
    long Unreadable,
    IReadOnlyDictionary<string, long> UnreadableByKind,
    IReadOnlyDictionary<string, long> DifferencesByField,
    IReadOnlyDictionary<string, long> NotInModelByField,
    IReadOnlyList<ObjectId> SampleApplicationIds
)
{
    public string ToLogLine() =>
        $"applications={Applications} matching={Matching} withDifferences={WithDifferences} "
        + $"notMigrated={NotMigrated} unreadable={Unreadable}";
}
