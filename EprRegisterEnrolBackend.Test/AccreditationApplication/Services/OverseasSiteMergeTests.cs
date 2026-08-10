using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-292 AC01/AC02. isNewSite drives the regulator's "new" badge, so a PATCH of the site list
// must not be able to set it, clear it, or wipe it by omission. Mirrors PrnsAuthoriserMergeTests.
public class OverseasSiteMergeTests
{
    private static OverseasSiteModel Site(
        int siteId,
        bool isNewSite = false,
        string siteName = "Site",
        InterimSiteModel? interimSite = null
    ) =>
        new()
        {
            SiteId = siteId,
            SiteName = siteName,
            IsNewSite = isNewSite,
            InterimSite = interimSite,
        };

    private static InterimSiteModel Interim(int siteId, bool isNewSite = false) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = $"SN-{siteId:D4}",
            Country = "France",
            SiteName = "Interim",
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Marie Curie",
            ContactEmail = "marie@example.com",
            ContactPhone = "0033111222333",
            IsNewSite = isNewSite,
        };

    [Fact]
    public void Merge_KnownSiteId_KeepsPersistedIsNewSite()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, isNewSite: true), Site(2, isNewSite: false)],
            [Site(1), Site(2)]
        );

        result[0].IsNewSite.Should().BeTrue();
        result[1].IsNewSite.Should().BeFalse();
    }

    [Fact]
    public void Merge_ClientOmitsIsNewSite_DoesNotFlipRegisteredSiteToNew()
    {
        // The live failure this guards: a ReEx-sourced registered site is IsNewSite false, and a
        // PATCH body that simply doesn't carry the flag must not promote it to new.
        var result = OverseasSiteMerge.Merge([Site(1, isNewSite: false)], [Site(1)]);

        result.Should().ContainSingle().Which.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public void Merge_ClientClaimsNewForKnownNotNewSite_CannotSetTheFlag()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, isNewSite: false)],
            [Site(1, isNewSite: true)]
        );

        result.Should().ContainSingle().Which.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public void Merge_ClientClaimsNotNewForKnownNewSite_CannotClearTheFlag()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, isNewSite: true)],
            [Site(1, isNewSite: false)]
        );

        result.Should().ContainSingle().Which.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public void Merge_UnknownSiteId_IsTreatedAsNew()
    {
        // Anomalous — sites are only created via the add-site endpoint — but it has never been
        // persisted, so it errs toward the regulator's attention, as unknown authoriser emails do.
        var result = OverseasSiteMerge.Merge(
            [Site(1, isNewSite: false)],
            [Site(99, isNewSite: false)]
        );

        result.Should().ContainSingle().Which.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public void Merge_EmptyPersistedList_TreatsEverySiteAsNew()
    {
        OverseasSiteMerge.Merge([], [Site(1), Site(2)]).Should().OnlyContain(s => s.IsNewSite);
    }

    [Fact]
    public void Merge_NullPersistedList_TreatsEverySiteAsNew()
    {
        OverseasSiteMerge.Merge(null, [Site(1)]).Should().ContainSingle().Which.IsNewSite.Should()
            .BeTrue();
    }

    [Fact]
    public void Merge_NullIncomingList_ReturnsEmptyList()
    {
        OverseasSiteMerge.Merge([Site(1)], null).Should().BeEmpty();
    }

    [Fact]
    public void Merge_BothListsNull_ReturnsEmptyList()
    {
        OverseasSiteMerge.Merge(null, null).Should().BeEmpty();
    }

    [Fact]
    public void Merge_EmptyIncomingList_ReturnsEmptyList()
    {
        OverseasSiteMerge.Merge([Site(1)], []).Should().BeEmpty();
    }

    [Fact]
    public void Merge_PersistedListHoldsDuplicateSiteIds_UsesFirstEntry()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, isNewSite: false), Site(1, isNewSite: true)],
            [Site(1)]
        );

        result.Should().ContainSingle().Which.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public void Merge_SiteOmittedFromIncoming_IsNotResurrected()
    {
        var result = OverseasSiteMerge.Merge([Site(1), Site(2)], [Site(1)]);

        result.Should().ContainSingle().Which.SiteId.Should().Be(1);
    }

    [Fact]
    public void Merge_TakesOperatorEnteredFieldsFromIncoming()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, isNewSite: true, siteName: "Old Name")],
            [Site(1, siteName: "Renamed Site")]
        );

        result[0].SiteName.Should().Be("Renamed Site");
        result[0].IsNewSite.Should().BeTrue();
    }

    // --- nested interim site ---

    [Fact]
    public void Merge_KnownInterimSiteId_KeepsPersistedIsNewSite()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, interimSite: Interim(2, isNewSite: true))],
            [Site(1, interimSite: Interim(2, isNewSite: false))]
        );

        result[0].InterimSite!.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public void Merge_ClientClaimsNewForKnownNotNewInterimSite_CannotSetTheFlag()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, interimSite: Interim(2, isNewSite: false))],
            [Site(1, interimSite: Interim(2, isNewSite: true))]
        );

        result[0].InterimSite!.IsNewSite.Should().BeFalse();
    }

    [Fact]
    public void Merge_InterimSiteNotPersistedBefore_IsTreatedAsNew()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1)],
            [Site(1, interimSite: Interim(2, isNewSite: false))]
        );

        result[0].InterimSite!.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public void Merge_InterimSiteIdChanged_IsTreatedAsNew()
    {
        // A different interim id under the same ORS is a different interim site, not an edit.
        var result = OverseasSiteMerge.Merge(
            [Site(1, interimSite: Interim(2, isNewSite: false))],
            [Site(1, interimSite: Interim(3, isNewSite: false))]
        );

        result[0].InterimSite!.IsNewSite.Should().BeTrue();
    }

    [Fact]
    public void Merge_NoInterimSiteOnEitherSide_DoesNotThrow()
    {
        OverseasSiteMerge.Merge([Site(1)], [Site(1)])[0].InterimSite.Should().BeNull();
    }

    [Fact]
    public void Merge_InterimSiteRemovedByClient_IsGenuinelyRemoved()
    {
        var result = OverseasSiteMerge.Merge(
            [Site(1, interimSite: Interim(2, isNewSite: true))],
            [Site(1)]
        );

        result[0].InterimSite.Should().BeNull();
    }

    [Fact]
    public void Merge_InterimIdLookupIsSeparateFromSiteIdLookup()
    {
        // Guards against collapsing the two id spaces into one map: site 2 is persisted as not
        // new, but interim id 2 under site 1 has never been persisted and must come out new.
        var result = OverseasSiteMerge.Merge(
            [Site(1), Site(2, isNewSite: false)],
            [Site(1, interimSite: Interim(2)), Site(2)]
        );

        result[0].InterimSite!.IsNewSite.Should().BeTrue();
    }

    // --- server-internal state a client can never supply ---

    [Fact]
    public void Merge_KnownSite_CarriesThePromoteRevertUndoStackAcross()
    {
        // PreviousSites is [JsonIgnore], so it never round-trips through the frontend. Without
        // this, saving the site list would destroy a promoted site's revert target.
        var persisted = Site(1);
        persisted.PreviousSites.Add(Site(1, siteName: "Pre-promotion"));

        var result = OverseasSiteMerge.Merge([persisted], [Site(1)]);

        result[0].PreviousSites.Should().ContainSingle();
        result[0].PreviousSites[0].SiteName.Should().Be("Pre-promotion");
    }

    [Fact]
    public void Merge_UnknownSite_HasNoUndoStack()
    {
        OverseasSiteMerge.Merge([Site(1)], [Site(99)])[0].PreviousSites.Should().BeEmpty();
    }

    // --- RegisteredNowAccredited (epr-zgrb) ---

    [Fact]
    public void Merge_KnownSite_KeepsPersistedRegisteredNowAccredited()
    {
        var persisted = Site(1);
        persisted.RegisteredNowAccredited = true;

        var result = OverseasSiteMerge.Merge([persisted], [Site(1)]);

        result[0].RegisteredNowAccredited.Should().BeTrue();
    }

    [Fact]
    public void Merge_ClientOmitsRegisteredNowAccredited_DoesNotUnPromote()
    {
        // Omission deserialises to false, which silently cleared the promotion and then broke
        // revert. The incoming site here carries the default false, standing in for that body.
        var persisted = Site(1);
        persisted.RegisteredNowAccredited = true;

        var incoming = Site(1);
        incoming.RegisteredNowAccredited.Should().BeFalse("precondition: the omitted-key state");

        OverseasSiteMerge.Merge([persisted], [incoming])[0]
            .RegisteredNowAccredited.Should()
            .BeTrue();
    }

    [Fact]
    public void Merge_ClientClaimsPromotedForUnpromotedSite_CannotSetTheFlag()
    {
        var incoming = Site(1);
        incoming.RegisteredNowAccredited = true;

        OverseasSiteMerge.Merge([Site(1)], [incoming])[0]
            .RegisteredNowAccredited.Should()
            .BeFalse();
    }

    [Fact]
    public void Merge_UnknownSite_CannotArrivePromoted()
    {
        // Promotion only ever happens via PromoteOverseasSite against an already-persisted site,
        // so a site the server has never seen cannot be promoted regardless of what is sent.
        var incoming = Site(99);
        incoming.RegisteredNowAccredited = true;

        OverseasSiteMerge.Merge([Site(1)], [incoming])[0]
            .RegisteredNowAccredited.Should()
            .BeFalse();
    }
}
