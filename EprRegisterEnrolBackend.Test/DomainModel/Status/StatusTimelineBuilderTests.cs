using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.DomainModel.Status;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.DomainModel.Status;

public class StatusTimelineBuilderTests
{
    private static readonly string[] Statuses = ["approved", "suspended", "cancelled", "withdrawn"];

    private static DateTime Utc(int year, int month, int day, int hour = 12) =>
        new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private static DateOnly Day(int year, int month, int day) => new(year, month, day);

    private static StatusTimelinePeriodDto Period(string status, DateOnly from, DateOnly? to) =>
        new()
        {
            Status = status,
            EffectiveFromDate = from,
            EffectiveToDate = to,
        };

    private static List<StatusHistoryEntryDto> WorkedExampleHistory() =>
        StatusHistoryBuilder.Build(
            [
                ("approved", Utc(2026, 11, 10)),
                ("suspended", Utc(2026, 12, 15)),
                ("approved", Utc(2027, 4, 1)),
                ("approved", Utc(2027, 6, 10)),
                ("cancelled", Utc(2027, 9, 1)),
            ],
            2027
        );

    [Fact]
    public void WorkedExample_HasTheHistoryInTheStory()
    {
        WorkedExampleHistory()
            .Select(e => (e.Status, e.EffectiveFromDate, e.EffectiveToDate))
            .Should()
            .Equal(
                ("approved", Day(2027, 1, 1), Day(2027, 1, 1)),
                ("suspended", Day(2027, 1, 1), Day(2027, 3, 31)),
                ("approved", Day(2027, 4, 1), Day(2027, 6, 9)),
                ("approved", Day(2027, 6, 10), Day(2027, 8, 31)),
                ("cancelled", Day(2027, 9, 1), null)
            );
    }

    [Fact]
    public void WorkedExample_GivesTheTimelineInTheStory()
    {
        var timeline = StatusTimelineBuilder.Build(WorkedExampleHistory());

        timeline
            .Should()
            .Equal(
                Period("suspended", Day(2027, 1, 1), Day(2027, 3, 31)),
                Period("approved", Day(2027, 4, 1), Day(2027, 8, 31)),
                Period("cancelled", Day(2027, 9, 1), null)
            );
        StatusTimelineBuilder.StatusOn(timeline, Day(2027, 1, 1)).Should().Be("suspended");
        StatusTimelineBuilder.StatusOn(timeline, Day(2026, 12, 31)).Should().BeNull();
    }

    [Theory]
    [InlineData("2027-03-31", "suspended")]
    [InlineData("2027-04-01", "approved")]
    [InlineData("2027-08-31", "approved")]
    [InlineData("2027-09-01", "cancelled")]
    [InlineData("2099-01-01", "cancelled")]
    public void StatusOn_TakesThePeriodContainingTheDate_EndsIncluded(string date, string expected)
    {
        var timeline = StatusTimelineBuilder.Build(WorkedExampleHistory());

        StatusTimelineBuilder.StatusOn(timeline, DateOnly.Parse(date)).Should().Be(expected);
    }

    [Fact]
    public void AnEmptyHistory_GivesAnEmptyTimeline_WithNoStatusOnAnyDate()
    {
        var timeline = StatusTimelineBuilder.Build([]);

        timeline.Should().BeEmpty();
        StatusTimelineBuilder.StatusOn(timeline, Day(2027, 1, 1)).Should().BeNull();
    }

    // An interim site's changes made before its overseas site's approval are all recorded at the approval,
    // so they share both keys, and the one later in the history wins.
    [Fact]
    public void EntriesSharingBothDateAndRecordedAt_TakeTheOneLaterInTheHistory()
    {
        var approval = Utc(2027, 5, 1);
        var history = StatusHistoryBuilder.Build(
            [("approved", approval), ("withdrawn", approval)],
            2027
        );

        StatusTimelineBuilder
            .Build(history)
            .Should()
            .Equal(Period("withdrawn", Day(2027, 5, 1), null));
    }

    [Fact]
    public void ARepeatedStatusThatIsOverruledOnItsDay_DoesNotSplitAMergedPeriod()
    {
        var history = StatusHistoryBuilder.Build(
            [
                ("approved", Utc(2027, 2, 1)),
                ("suspended", Utc(2027, 3, 1, 9)),
                ("approved", Utc(2027, 3, 1, 10)),
            ],
            2027
        );

        StatusTimelineBuilder
            .Build(history)
            .Should()
            .Equal(Period("approved", Day(2027, 2, 1), null));
    }

    // Property-based: many random histories from a fixed seed, so a failure repeats. Few days and few
    // recording times make shared dates, shared recordedAt and repeated statuses common.
    private static IEnumerable<(int Seed, List<StatusHistoryEntryDto> History)> RandomHistories()
    {
        for (var seed = 0; seed < 2000; seed++)
        {
            var random = new Random(seed);
            var changes = Enumerable
                .Range(0, random.Next(1, 10))
                .Select(_ =>
                    (
                        Statuses[random.Next(Statuses.Length)],
                        // From before the accreditation year (so several start on 1 January) to within it.
                        Utc(2026, 12, 28, 0).AddHours(random.Next(0, 12) * 12)
                    )
                )
                .ToList();
            yield return (seed, StatusHistoryBuilder.Build(changes, 2027));
        }
    }

    // The rule applied straight to the history: of the entries started by the date, the latest start,
    // then the latest recordedAt (the later in the history where they share it).
    private static string? StatusFromHistory(
        IReadOnlyList<StatusHistoryEntryDto> history,
        DateOnly date
    ) =>
        history
            .Where(entry => entry.EffectiveFromDate <= date)
            .OrderBy(entry => entry.EffectiveFromDate)
            .ThenBy(entry => entry.RecordedAt)
            .LastOrDefault()
            ?.Status;

    [Fact]
    public void ForAnyHistory_TheStatusOnAnyDate_IsTheSameFromTheTimelineAndTheHistory()
    {
        foreach (var (seed, history) in RandomHistories())
        {
            var timeline = StatusTimelineBuilder.Build(history);
            var first = history.Min(e => e.EffectiveFromDate).AddDays(-2);
            var last = history.Max(e => e.EffectiveFromDate).AddDays(2);

            for (var date = first; date <= last; date = date.AddDays(1))
            {
                StatusTimelineBuilder
                    .StatusOn(timeline, date)
                    .Should()
                    .Be(StatusFromHistory(history, date), $"seed {seed}, date {date}");
            }
        }
    }

    [Fact]
    public void ForAnyHistory_TheTimelineHasNoGapsOverlapsOrRepeats_AndEndsOnTheLatestStatus()
    {
        foreach (var (seed, history) in RandomHistories())
        {
            var because = $"seed {seed}";
            var timeline = StatusTimelineBuilder.Build(history);

            timeline[0].EffectiveFromDate.Should().Be(history.Min(e => e.EffectiveFromDate), because);
            timeline[^1].EffectiveToDate.Should().BeNull(because);
            timeline[^1]
                .Status.Should()
                .Be(StatusHistoryBuilder.Latest(history).Status, because);

            for (var i = 0; i + 1 < timeline.Count; i++)
            {
                timeline[i].EffectiveToDate.Should().NotBeNull(because);
                timeline[i]
                    .EffectiveToDate!.Value.Should()
                    .BeOnOrAfter(timeline[i].EffectiveFromDate, because);
                timeline[i + 1]
                    .EffectiveFromDate.Should()
                    .Be(timeline[i].EffectiveToDate!.Value.AddDays(1), because);
                timeline[i + 1].Status.Should().NotBe(timeline[i].Status, because);
            }
        }
    }

    [Fact]
    public void Build_LeavesTheHistoryAsItWas()
    {
        var history = WorkedExampleHistory();
        var before = history.ToList();

        StatusTimelineBuilder.Build(history);

        history.Should().Equal(before);
    }
}
