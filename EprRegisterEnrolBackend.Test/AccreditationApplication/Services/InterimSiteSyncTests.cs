using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-603. An ORS holds many interim sites in InterimSites; the legacy singular
// InterimSite is kept as a mirror so consumers that have not moved to the list
// keep working. These tests pin the two rules that mirror has to obey:
// it is derived, never authoritative, and it points at the first site the
// operator has NOT withdrawn.
public class InterimSiteSyncTests
{
    private static InterimSiteModel Interim(
        int siteId,
        bool isNewSite = false,
        DateTime? createdAt = null,
        DateTime? removedAt = null,
        string siteName = "Interim"
    ) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = $"SN-{siteId:D4}",
            Country = "France",
            SiteName = siteName,
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Marie Curie",
            ContactEmail = "marie@example.com",
            ContactPhone = "0033111222333",
            OperationCodes = ["R12"],
            IsNewSite = isNewSite,
            CreatedAt = createdAt,
            RemovedAt = removedAt,
        };

    private static OverseasSiteModel Site(
        InterimSiteModel? mirror = null,
        List<InterimSiteModel>? list = null
    ) =>
        new()
        {
            SiteId = 1,
            SiteName = "ORS",
            InterimSite = mirror,
            InterimSites = list ?? [],
        };

    // ── Normalise ────────────────────────────────────────────────────────────

    [Fact]
    public void Normalise_SingularOnly_ProducesAOneElementList()
    {
        var site = Site(mirror: Interim(42));

        InterimSiteSync.Normalise(site);

        site.InterimSites.Should().ContainSingle();
        site.InterimSites[0].SiteId.Should().Be(42);
    }

    // The answer to part 1's open question: a migrated interim site keeps the
    // isNewSite it was persisted with. Nothing re-derives it.
    [Fact]
    public void Normalise_SingularOnly_CarriesIsNewSiteOverVerbatim()
    {
        var site = Site(mirror: Interim(42, isNewSite: true));

        InterimSiteSync.Normalise(site);

        site.InterimSites[0].IsNewSite.Should().BeTrue();
    }

    // CreatedAt exists for auditability, so a document that predates the field
    // must read as "unknown" rather than be stamped with an invented time.
    [Fact]
    public void Normalise_SingularOnly_LeavesCreatedAtNull()
    {
        var site = Site(mirror: Interim(42));

        InterimSiteSync.Normalise(site);

        site.InterimSites[0].CreatedAt.Should().BeNull();
    }

    [Fact]
    public void Normalise_ListAlreadyPopulated_DoesNotAppendTheMirrorAgain()
    {
        var site = Site(mirror: Interim(42), list: [Interim(42), Interim(43)]);

        InterimSiteSync.Normalise(site);

        site.InterimSites.Should().HaveCount(2);
    }

    [Fact]
    public void Normalise_NoInterimSiteAtAll_LeavesTheListEmpty()
    {
        var site = Site();

        InterimSiteSync.Normalise(site);

        site.InterimSites.Should().BeEmpty();
    }

    [Fact]
    public void Normalise_RunTwice_IsIdempotent()
    {
        var site = Site(mirror: Interim(42));

        InterimSiteSync.Normalise(site);
        InterimSiteSync.Normalise(site);

        site.InterimSites.Should().ContainSingle();
    }

    // ── SyncMirror ───────────────────────────────────────────────────────────

    [Fact]
    public void SyncMirror_PointsTheMirrorAtTheFirstEntry()
    {
        var site = Site(list: [Interim(42), Interim(43)]);

        InterimSiteSync.SyncMirror(site);

        site.InterimSite!.SiteId.Should().Be(42);
    }

    // The rule that makes the mirror safe: a legacy consumer must never be
    // shown a site the operator has withdrawn.
    [Fact]
    public void SyncMirror_FirstEntryWithdrawn_SkipsToTheFirstActiveOne()
    {
        var site = Site(
            list: [Interim(42, removedAt: DateTime.UtcNow), Interim(43)]
        );

        InterimSiteSync.SyncMirror(site);

        site.InterimSite!.SiteId.Should().Be(43);
    }

    [Fact]
    public void SyncMirror_EveryEntryWithdrawn_ClearsTheMirror()
    {
        var site = Site(
            mirror: Interim(42),
            list:
            [
                Interim(42, removedAt: DateTime.UtcNow),
                Interim(43, removedAt: DateTime.UtcNow),
            ]
        );

        InterimSiteSync.SyncMirror(site);

        site.InterimSite.Should().BeNull();
    }

    [Fact]
    public void SyncMirror_EmptyList_ClearsTheMirror()
    {
        var site = Site(mirror: Interim(42));

        InterimSiteSync.SyncMirror(site);

        site.InterimSite.Should().BeNull();
    }

    // ── Active ───────────────────────────────────────────────────────────────

    [Fact]
    public void Active_ExcludesWithdrawnEntriesAndKeepsOrder()
    {
        var site = Site(
            list:
            [
                Interim(42),
                Interim(43, removedAt: DateTime.UtcNow),
                Interim(44),
            ]
        );

        InterimSiteSync
            .Active(site)
            .Select(i => i.SiteId)
            .Should()
            .Equal(42, 44);
    }

    [Fact]
    public void Active_NoInterimSites_IsEmpty()
    {
        InterimSiteSync.Active(Site()).Should().BeEmpty();
    }
}
