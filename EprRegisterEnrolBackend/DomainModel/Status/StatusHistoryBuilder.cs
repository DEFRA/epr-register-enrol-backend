using EprRegisterEnrolBackend.AccreditationApi;

namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>
/// The status history of an accreditation, an overseas site or an interim site, from the status changes
/// recorded for it. A change applies from the later of 1 January of the accreditation year and the UK date it
/// was recorded (a status recorded before the year starts with the year), and to the day before the next
/// change applies from, so the latest has no end. Entries are ordered by when they apply from, and where two
/// apply from the same date the one recorded later comes last, so it takes precedence, and the one recorded
/// earlier ends on that same date: it is kept in the history, but its period is never before its start. RA-772.
/// </summary>
public static class StatusHistoryBuilder
{
    public static List<StatusHistoryEntryDto> Build(
        IEnumerable<(string Status, DateTime RecordedAt)> changes,
        int accreditationYear
    )
    {
        var startOfYear = new DateOnly(accreditationYear, 1, 1);
        var dated = changes.Select(change =>
            (
                change.Status,
                change.RecordedAt,
                From: Later(startOfYear, UkCalendar.ToUkDate(change.RecordedAt))
            )
        );
        var ordered = InPrecedenceOrder(dated, change => change.From, change => change.RecordedAt)
            .ToList();

        return ordered
            .Select(
                (change, index) =>
                    new StatusHistoryEntryDto
                    {
                        Status = change.Status,
                        EffectiveFromDate = change.From,
                        EffectiveToDate =
                            index + 1 < ordered.Count
                                ? Later(change.From, ordered[index + 1].From.AddDays(-1))
                                : null,
                        RecordedAt = change.RecordedAt,
                    }
            )
            .ToList();
    }

    /// <summary>
    /// The latest entry of a status history: the latest <c>effectiveFromDate</c>, and the later
    /// <c>recordedAt</c> where two share one. Whether that date has arrived does not matter. It is the
    /// entry itself, so its dates always match the history's. It orders the history itself, so it works on any
    /// history, not only one from <see cref="Build"/>. The history must not be empty.
    /// </summary>
    public static StatusHistoryEntryDto Latest(IReadOnlyList<StatusHistoryEntryDto> history) =>
        InPrecedenceOrder(history).Last();

    /// <summary>
    /// A history in order of precedence, lowest first: by <c>effectiveFromDate</c>, then <c>recordedAt</c>.
    /// The sort is stable, so entries sharing both keep their order and the later one takes precedence.
    /// <see cref="Latest"/> and <see cref="StatusTimelineBuilder"/> both use it, so the timeline's last
    /// period always has the latest status.
    /// </summary>
    internal static IOrderedEnumerable<StatusHistoryEntryDto> InPrecedenceOrder(
        IEnumerable<StatusHistoryEntryDto> history
    ) => InPrecedenceOrder(history, entry => entry.EffectiveFromDate, entry => entry.RecordedAt);

    private static IOrderedEnumerable<T> InPrecedenceOrder<T>(
        IEnumerable<T> items,
        Func<T, DateOnly> effectiveFrom,
        Func<T, DateTime> recordedAt
    ) => items.OrderBy(effectiveFrom).ThenBy(recordedAt);

    private static DateOnly Later(DateOnly a, DateOnly b) => a >= b ? a : b;
}
