using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

// RA-316. These fees are a mirror of the legacy frontend's paymentDetails.js — the expected
// values below are written out as literal pence rather than derived from the calculator's own
// constants on purpose, so that changing a constant fails a test instead of silently moving the
// answer. If a fee legitimately changes, the frontend helper must change in the same breath.
public class AccreditationChargeCalculatorTests
{
    private static AccreditationApplicationModel CreateApplication(
        PlannedTonnageBand? band,
        params bool[] siteSelectedFlags
    )
    {
        var application = new AccreditationApplicationModel
        {
            OrganisationId = "12345",
            Year = 2026,
            MaterialType = MaterialType.Plastic,
            Prns = new AccreditationApplicationPrns { PlannedTonnageBand = band },
        };

        if (siteSelectedFlags.Length > 0)
        {
            application.OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = siteSelectedFlags
                    .Select(
                        (selected, index) =>
                            new OverseasSiteModel
                            {
                                SiteId = index + 1,
                                SiteName = $"Site {index + 1}",
                                Selected = selected,
                            }
                    )
                    .ToList(),
            };
        }

        return application;
    }

    // Every band in the table, with no overseas sites: the tonnage fee alone, in pence.
    [Theory]
    [InlineData(PlannedTonnageBand.UpTo500, 54_600)]
    [InlineData(PlannedTonnageBand.UpTo1000, 218_400)]
    [InlineData(PlannedTonnageBand.UpTo10000, 327_600)]
    [InlineData(PlannedTonnageBand.Over10000, 396_500)]
    public void EveryTonnageBand_NoSites_ReturnsBandFeeInPence(
        PlannedTonnageBand band,
        int expectedPence
    )
    {
        AccreditationChargeCalculator
            .CalculateChargePence(CreateApplication(band))
            .Should()
            .Be(expectedPence);
    }

    [Fact]
    public void TonnageBandTable_CoversEveryDeclaredBand()
    {
        // Guards the missing-band path from quietly becoming the DEFAULT path: adding a band to
        // the enum without adding its fee would otherwise just start omitting the charge.
        AccreditationChargeCalculator
            .TonnageFeesPounds.Keys.Should()
            .BeEquivalentTo(Enum.GetValues<PlannedTonnageBand>());
    }

    [Fact]
    public void NoTonnageBand_ReturnsNull_SoTheChargeIsOmittedRatherThanWrong()
    {
        // The frontend's tonnageFeeCalculator throws here. Throwing in this backend would fail
        // the whole submission to ManagementBe over a display-only field, so we omit instead.
        AccreditationChargeCalculator.CalculateChargePence(CreateApplication(null)).Should().BeNull();
    }

    [Fact]
    public void NoTonnageBand_WithSelectedSites_StillReturnsNull_NotASitesOnlyTotal()
    {
        // A sites-only figure would be plausible but understated, and would be displayed to the
        // regulator as if it were the real charge. Blank is safer than wrong.
        AccreditationChargeCalculator
            .CalculateChargePence(CreateApplication(null, true, true))
            .Should()
            .BeNull();
    }

    [Fact]
    public void UnknownTonnageBand_ReturnsNull_RatherThanThrowing()
    {
        // A band value outside the enum's declared range — e.g. a document written by a newer
        // version of the service, or a corrupt BSON value. Must not take a submission down.
        var rogueBand = (PlannedTonnageBand)999;

        AccreditationChargeCalculator.CalculateChargePence(rogueBand, 2).Should().BeNull();
    }

    [Fact]
    public void ZeroSites_ChargesTonnageFeeOnly()
    {
        AccreditationChargeCalculator
            .CalculateChargePence(CreateApplication(PlannedTonnageBand.UpTo10000))
            .Should()
            .Be(327_600);
    }

    [Fact]
    public void NullOverseasSitesSection_IsTreatedAsZeroSites()
    {
        var application = CreateApplication(PlannedTonnageBand.UpTo10000);
        application.OverseasSites = null;

        AccreditationChargeCalculator.CalculateChargePence(application).Should().Be(327_600);
    }

    [Fact]
    public void SingleSelectedSite_AddsOneSiteFee()
    {
        // 3276 + 328 = 3604
        AccreditationChargeCalculator
            .CalculateChargePence(CreateApplication(PlannedTonnageBand.UpTo10000, true))
            .Should()
            .Be(360_400);
    }

    [Fact]
    public void MultipleSelectedSites_AddOneSiteFeeEach()
    {
        // 3276 + (328 * 3) = 4260
        AccreditationChargeCalculator
            .CalculateChargePence(CreateApplication(PlannedTonnageBand.UpTo10000, true, true, true))
            .Should()
            .Be(426_000);
    }

    [Fact]
    public void DeselectedSites_AreExcludedFromTheCharge()
    {
        // 3276 + (328 * 2 selected) = 3932 — the two deselected sites contribute nothing.
        AccreditationChargeCalculator
            .CalculateChargePence(
                CreateApplication(PlannedTonnageBand.UpTo10000, true, false, true, false)
            )
            .Should()
            .Be(393_200);
    }

    [Fact]
    public void AllSitesDeselected_ChargesTonnageFeeOnly()
    {
        AccreditationChargeCalculator
            .CalculateChargePence(CreateApplication(PlannedTonnageBand.UpTo10000, false, false))
            .Should()
            .Be(327_600);
    }

    [Fact]
    public void SiteWithNoStoredSelectedFlag_Counts()
    {
        // The frontend filters `selected !== false`, so a site with no stored flag is charged for.
        // Selected is non-nullable and defaults to true, which is the exact equivalent — this
        // pins that default, because flipping it would silently undercharge every legacy document.
        var application = CreateApplication(PlannedTonnageBand.UpTo500);
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Defaulted" }],
        };

        // 546 + 328 = 874
        AccreditationChargeCalculator.CalculateChargePence(application).Should().Be(87_400);
    }

    [Fact]
    public void EmptySiteList_ChargesTonnageFeeOnly()
    {
        var application = CreateApplication(PlannedTonnageBand.Over10000);
        application.OverseasSites = new AccreditationApplicationOverseasSites { Sites = [] };

        AccreditationChargeCalculator.CalculateChargePence(application).Should().Be(396_500);
    }

    [Fact]
    public void ChargeIsAlwaysAWholeNumberOfPence_WithNoRoundingLoss()
    {
        // Fees are whole pounds, so every result must land on an exact 100-pence boundary.
        foreach (var band in Enum.GetValues<PlannedTonnageBand>())
        {
            for (var sites = 0; sites <= 5; sites++)
            {
                var pence = AccreditationChargeCalculator.CalculateChargePence(band, sites);
                pence.Should().NotBeNull();
                (pence!.Value % 100).Should().Be(0, "band {0} with {1} sites", band, sites);
            }
        }
    }

    [Fact]
    public void NullApplication_Throws()
    {
        var act = () => AccreditationChargeCalculator.CalculateChargePence(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
