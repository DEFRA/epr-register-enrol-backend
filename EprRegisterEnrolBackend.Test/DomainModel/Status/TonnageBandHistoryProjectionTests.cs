using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Status;

namespace EprRegisterEnrolBackend.Test.DomainModel.Status;

public class TonnageBandHistoryProjectionTests
{
    private static AccreditationEntity Version(
        long version,
        TonnageBand? band,
        AccreditationStatus status = AccreditationStatus.Approved
    ) =>
        new()
        {
            Version = version,
            ValidFrom = Day(version),
            TonnageBand = band,
            Status = status,
        };

    private static DateTime Day(long version) =>
        new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc).AddDays(version);

    [Fact]
    public void ForAccreditation_SingleVersion_GivesOneEntry()
    {
        var entry = Assert.Single(
            TonnageBandHistoryProjection.ForAccreditation([Version(1, TonnageBand.UpTo500)])
        );

        Assert.Equal("up_to_500", entry.TonnageBand);
        Assert.Equal(Day(1), entry.RecordedAt);
    }

    [Fact]
    public void ForAccreditation_ListsOnlyTheBandChanges()
    {
        var history = TonnageBandHistoryProjection.ForAccreditation([
            Version(1, TonnageBand.UpTo500),
            Version(2, TonnageBand.UpTo500, AccreditationStatus.Suspended),
            Version(3, TonnageBand.UpTo5000),
        ]);

        Assert.Equal(["up_to_500", "up_to_5000"], history.Select(e => e.TonnageBand));
        Assert.Equal([Day(1), Day(3)], history.Select(e => e.RecordedAt));
    }

    [Fact]
    public void ForAccreditation_VersionsWithoutABand_AreSkipped()
    {
        var entry = Assert.Single(
            TonnageBandHistoryProjection.ForAccreditation([
                Version(1, null),
                Version(2, TonnageBand.Over10000),
            ])
        );

        Assert.Equal("over_10000", entry.TonnageBand);
        Assert.Equal(Day(2), entry.RecordedAt);
    }

    [Fact]
    public void ForAccreditation_NoBandEver_IsEmpty() =>
        Assert.Empty(
            TonnageBandHistoryProjection.ForAccreditation([Version(1, null), Version(2, null)])
        );
}
