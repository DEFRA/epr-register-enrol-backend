using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Status;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Status;

public class StatusHistoryProjectionTests
{
    private static readonly ObjectId s_siteId = ObjectId.GenerateNewId();

    private static AccreditationEntity Version(
        long version,
        AccreditationStatus status,
        params AccreditationOverseasSiteEntry[] sites
    ) => Version(version, Day(version), status, sites);

    private static AccreditationEntity Version(
        long version,
        DateTime validFrom,
        AccreditationStatus status,
        params AccreditationOverseasSiteEntry[] sites
    ) =>
        new()
        {
            Version = version,
            Year = 2026,
            ValidFrom = validFrom,
            Status = status,
            AccreditationOverseasSites = [.. sites],
        };

    private static DateTime Day(long version) =>
        new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc).AddDays(version);

    private static AccreditationOverseasSiteEntry Site(AccreditedSiteStatus status) =>
        new() { OverseasSiteId = s_siteId, Status = status };

    [Fact]
    public void ForAccreditation_SingleVersion_GivesOneEntry()
    {
        var recorded = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);

        var history = StatusHistoryProjection.ForAccreditation([
            Version(1, recorded, AccreditationStatus.Approved),
        ]);

        var entry = Assert.Single(history);
        Assert.Equal("approved", entry.Status);
        Assert.Equal(recorded, entry.RecordedAt);
        Assert.Equal(new DateOnly(2026, 1, 15), entry.EffectiveFromDate);
        Assert.Null(entry.EffectiveToDate);
    }

    [Fact]
    public void ForAccreditation_EachEntryApplies_UntilTheDayBeforeTheNext()
    {
        var history = StatusHistoryProjection.ForAccreditation([
            Version(1, new DateTime(2026, 2, 1, 10, 0, 0, DateTimeKind.Utc), AccreditationStatus.Approved),
            Version(2, new DateTime(2026, 4, 10, 10, 0, 0, DateTimeKind.Utc), AccreditationStatus.Suspended),
            Version(3, new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc), AccreditationStatus.Approved),
        ]);

        Assert.Equal(
            [new DateOnly(2026, 4, 9), new DateOnly(2026, 5, 31), (DateOnly?)null],
            history.Select(e => e.EffectiveToDate)
        );
    }

    [Fact]
    public void ForAccreditation_RecordedBeforeTheYear_AppliesFromThe1stOfJanuary()
    {
        var entry = Assert.Single(
            StatusHistoryProjection.ForAccreditation([
                Version(1, new DateTime(2025, 11, 20, 10, 0, 0, DateTimeKind.Utc), AccreditationStatus.Approved),
            ])
        );

        Assert.Equal(new DateOnly(2026, 1, 1), entry.EffectiveFromDate);
    }

    [Fact]
    public void ForAccreditation_SuspendAndUnsuspend_ListsOnlyTheStatusChanges()
    {
        var history = StatusHistoryProjection.ForAccreditation([
            Version(1, AccreditationStatus.Approved),
            Version(2, AccreditationStatus.Approved),
            Version(3, AccreditationStatus.Suspended),
            Version(4, AccreditationStatus.Approved),
        ]);

        Assert.Equal(["approved", "suspended", "approved"], history.Select(e => e.Status));
        Assert.Equal([Day(1), Day(3), Day(4)], history.Select(e => e.RecordedAt));
    }

    [Fact]
    public void ForAccreditation_VersionsOutOfOrder_AreProjectedInVersionOrder()
    {
        var history = StatusHistoryProjection.ForAccreditation([
            Version(2, AccreditationStatus.Suspended),
            Version(1, AccreditationStatus.Approved),
        ]);

        Assert.Equal(["approved", "suspended"], history.Select(e => e.Status));
    }

    [Fact]
    public void ForAccreditation_NoVersions_IsEmpty() =>
        Assert.Empty(StatusHistoryProjection.ForAccreditation([]));

    [Theory]
    [InlineData("2026-06-30T23:30:00Z", "2026-07-01")] // BST: already the next day in the UK
    [InlineData("2026-01-31T23:30:00Z", "2026-01-31")] // GMT
    public void ForAccreditation_EffectiveFromDate_IsTheUkDateItWasRecorded(
        string recorded,
        string expected
    )
    {
        var instant = DateTime.Parse(
            recorded,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
        );

        var entry = Assert.Single(
            StatusHistoryProjection.ForAccreditation([
                Version(1, instant, AccreditationStatus.Approved),
            ])
        );

        Assert.Equal(DateOnly.Parse(expected), entry.EffectiveFromDate);
    }

    [Fact]
    public void ForAccreditedSite_ListsOnlyTheSiteStatusChanges()
    {
        var history = StatusHistoryProjection.ForAccreditedSite(
            [
                Version(1, AccreditationStatus.Approved, Site(AccreditedSiteStatus.Approved)),
                // The accreditation's own status changes; the site's does not.
                Version(2, AccreditationStatus.Suspended, Site(AccreditedSiteStatus.Approved)),
                Version(3, AccreditationStatus.Approved, Site(AccreditedSiteStatus.Suspended)),
                Version(4, AccreditationStatus.Approved, Site(AccreditedSiteStatus.Approved)),
            ],
            s_siteId
        );

        Assert.Equal(["approved", "suspended", "approved"], history.Select(e => e.Status));
        Assert.Equal([Day(1), Day(3), Day(4)], history.Select(e => e.RecordedAt));
    }

    [Fact]
    public void ForAccreditedSite_SiteMissingThenBackUnchanged_IsNotRepeated()
    {
        var history = StatusHistoryProjection.ForAccreditedSite(
            [
                Version(1, AccreditationStatus.Approved, Site(AccreditedSiteStatus.Approved)),
                Version(2, AccreditationStatus.Approved),
                Version(3, AccreditationStatus.Approved, Site(AccreditedSiteStatus.Approved)),
            ],
            s_siteId
        );

        Assert.Equal(Day(1), Assert.Single(history).RecordedAt);
    }

    [Fact]
    public void ForAccreditedSite_UnknownSite_IsEmpty() =>
        Assert.Empty(
            StatusHistoryProjection.ForAccreditedSite(
                [Version(1, AccreditationStatus.Approved, Site(AccreditedSiteStatus.Approved))],
                ObjectId.GenerateNewId()
            )
        );
}
