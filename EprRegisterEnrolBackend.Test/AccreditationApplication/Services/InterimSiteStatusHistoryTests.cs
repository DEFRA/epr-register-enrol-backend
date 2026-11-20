using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-781: the status changes stored on an interim site.
public class InterimSiteStatusHistoryTests
{
    private static readonly DateTime Created = new(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Withdrawn = new(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static InterimSiteModel Interim(DateTime? createdAt = null, DateTime? removedAt = null) =>
        new()
        {
            SiteId = 2,
            SiteNumber = "002",
            Country = "France",
            SiteName = "Interim",
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "n",
            ContactEmail = "e@example.com",
            ContactPhone = "1",
            CreatedAt = createdAt,
            RemovedAt = removedAt,
        };

    private static (string, DateTime)[] History(InterimSiteModel interim) =>
        interim.StatusHistory.Select(h => (h.Status, h.RecordedAt)).ToArray();

    [Fact]
    public void Added_IsApprovedFromWhenTheSiteIsAdded()
    {
        InterimSiteStatusHistory
            .Added(Created)
            .Select(h => (h.Status, h.RecordedAt))
            .Should()
            .Equal(("approved", Created));
    }

    [Fact]
    public void Withdraw_RecordsTheWithdrawal_AndSetsRemovedAt()
    {
        var interim = Interim(createdAt: Created);

        InterimSiteStatusHistory.Withdraw(interim, Now);

        interim.RemovedAt.Should().Be(Now);
        History(interim).Should().Equal(("approved", Created), ("withdrawn", Now));
    }

    [Fact]
    public void Withdraw_OfAWithdrawnSite_ChangesNothing()
    {
        var interim = Interim(createdAt: Created, removedAt: Withdrawn);

        InterimSiteStatusHistory.Withdraw(interim, Now);

        interim.RemovedAt.Should().Be(Withdrawn);
        interim.StatusHistory.Should().BeEmpty();
    }

    [Fact]
    public void Restore_RecordsTheRestore_AndKeepsTheWithdrawal()
    {
        var interim = Interim(createdAt: Created);
        InterimSiteStatusHistory.Withdraw(interim, Withdrawn);

        InterimSiteStatusHistory.Restore(interim, Now);

        interim.RemovedAt.Should().BeNull();
        History(interim).Should().Equal(("approved", Created), ("withdrawn", Withdrawn), ("approved", Now));
    }

    // A site withdrawn before the history was stored: it knows approved and its withdrawal.
    [Fact]
    public void Restore_OfASiteWithNoHistory_SeedsItFromCreatedAtAndRemovedAt()
    {
        var interim = Interim(createdAt: Created, removedAt: Withdrawn);

        InterimSiteStatusHistory.Restore(interim, Now);

        History(interim).Should().Equal(("approved", Created), ("withdrawn", Withdrawn), ("approved", Now));
    }

    [Fact]
    public void Restore_OfAnActiveSite_ChangesNothing()
    {
        var interim = Interim(createdAt: Created);

        InterimSiteStatusHistory.Restore(interim, Now);

        interim.StatusHistory.Should().BeEmpty();
    }

    // CreatedAt is null for documents that predate it: the nearest date there is, not an invented one.
    [Fact]
    public void Seed_WithNoCreatedAt_UsesTheWithdrawalThenTheChange()
    {
        var withdrawn = Interim(removedAt: Withdrawn);
        var active = Interim();

        InterimSiteStatusHistory.Seed(withdrawn, Now);
        InterimSiteStatusHistory.Seed(active, Now);

        History(withdrawn).Should().Equal(("approved", Withdrawn), ("withdrawn", Withdrawn));
        History(active).Should().Equal(("approved", Now));
    }

    [Fact]
    public void Seed_OfASiteThatHasAHistory_ChangesNothing()
    {
        var interim = Interim(createdAt: Created);
        interim.StatusHistory = InterimSiteStatusHistory.Added(Created);

        InterimSiteStatusHistory.Seed(interim, Now);

        History(interim).Should().Equal(("approved", Created));
    }
}
