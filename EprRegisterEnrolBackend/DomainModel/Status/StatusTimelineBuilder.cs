using EprRegisterEnrolBackend.AccreditationApi;

namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>
/// A status history flattened into periods that don't overlap, so each date from the first period on has
/// exactly one status (RA-809). The history can have two entries on one date (see
/// <see cref="StatusHistoryBuilder"/>): the one recorded later wins, and the other never applies on its own,
/// so it is left out. It is not a history: the history stays the record of every change.
/// </summary>
public static class StatusTimelineBuilder
{
    /// <summary>
    /// The timeline of a history: of the entries sharing an <c>effectiveFromDate</c>, the one with the latest
    /// <c>recordedAt</c> (and, where they share that too, the one later in the history, as
    /// <see cref="StatusHistoryBuilder.Latest"/> takes it); one period for each, ending the day before the next
    /// starts, the last with no end; and adjacent periods with the same status merged into one. An empty
    /// history gives an empty timeline.
    /// </summary>
    public static List<StatusTimelinePeriodDto> Build(IReadOnlyList<StatusHistoryEntryDto> history)
    {
        // Of the entries sharing a date, the one with precedence is last.
        var kept = StatusHistoryBuilder
            .InPrecedenceOrder(history)
            .GroupBy(entry => entry.EffectiveFromDate)
            .Select(sameDay => sameDay.Last())
            .ToList();

        // Merging before working out the ends: a period starts only where the status changes.
        var starts = kept.Where((entry, index) => index == 0 || entry.Status != kept[index - 1].Status)
            .ToList();

        return starts
            .Select(
                (entry, index) =>
                    new StatusTimelinePeriodDto
                    {
                        Status = entry.Status,
                        EffectiveFromDate = entry.EffectiveFromDate,
                        EffectiveToDate =
                            index + 1 < starts.Count
                                ? starts[index + 1].EffectiveFromDate.AddDays(-1)
                                : null,
                    }
            )
            .ToList();
    }

    /// <summary>
    /// The status on a date: that of the timeline period containing it, or <c>null</c> if the date is before
    /// the first period (or the timeline is empty).
    /// </summary>
    public static string? StatusOn(IReadOnlyList<StatusTimelinePeriodDto> timeline, DateOnly date) =>
        timeline
            .FirstOrDefault(period =>
                period.EffectiveFromDate <= date
                && (period.EffectiveToDate is null || date <= period.EffectiveToDate)
            )
            ?.Status;
}
