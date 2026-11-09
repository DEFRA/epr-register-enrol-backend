using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Reconciliation;

/// <summary>
/// Counts, field paths and application ids only: no names, addresses, contact details or values, so a report is safe
/// to log. <see cref="AcceptedConflicts"/> are applications whose every difference is explained by a conflict listed for
/// them in <c>migrationConflicts</c>; they are counted apart and not in the fields below. Phase 5 needs <see cref="WithDifferences"/>, <see cref="NotMigrated"/> and <see cref="Unreadable"/> at zero.
/// </summary>
public sealed record ReconciliationReport(
    long Applications,
    long Matching,
    long WithDifferences,
    long AcceptedConflicts,
    long NotMigrated,
    long Unreadable,
    IReadOnlyDictionary<string, long> UnreadableByKind,
    IReadOnlyDictionary<string, long> DifferencesByField,
    IReadOnlyDictionary<string, long> ModelOnlyByField,
    IReadOnlyList<ObjectId> SampleApplicationIds
)
{
    public string ToLogLine() =>
        $"applications={Applications} matching={Matching} withDifferences={WithDifferences} acceptedConflicts={AcceptedConflicts} "
        + $"notMigrated={NotMigrated} unreadable={Unreadable}";
}
