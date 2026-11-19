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
        var ordered = changes
            .Select(change =>
                (
                    change.Status,
                    change.RecordedAt,
                    From: Later(startOfYear, UkCalendar.ToUkDate(change.RecordedAt))
                )
            )
            .OrderBy(change => change.From)
            .ThenBy(change => change.RecordedAt)
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
        history.OrderBy(entry => entry.EffectiveFromDate).ThenBy(entry => entry.RecordedAt).Last();

    private static DateOnly Later(DateOnly a, DateOnly b) => a >= b ? a : b;
}
