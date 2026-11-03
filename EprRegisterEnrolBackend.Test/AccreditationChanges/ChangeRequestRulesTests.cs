using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationChanges;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

public class ChangeRequestRulesTests
{
    private static OverseasSiteModel Site(
        string? country = null,
        string? coordinates = null,
        List<string>? operationCodes = null,
        bool? conditionsOfExport = null,
        bool isEu = false,
        bool isOecd = false
    ) =>
        new()
        {
            SiteId = 1,
            SiteName = "Site",
            Country = country,
            Coordinates = coordinates,
            OperationCodes = operationCodes ?? [],
            ConditionsOfExport = conditionsOfExport,
            IsEu = isEu,
            IsOecd = isOecd,
            Selected = false,
        };

    // AC04: £328 per ORS, no tonnage fee.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 32800)]
    [InlineData(3, 98400)]
    public void Charge_Is328PoundsPerSite(int sites, int expectedPence)
    {
        var charge = ChangeRequestRules.Charge(sites);

        charge.ChargeableSites.Should().Be(sites);
        charge.ChargePerSitePence.Should().Be(AccreditationChargeCalculator.OverseasSiteFeePounds * 100);
        charge.TotalPence.Should().Be(expectedPence);
    }

    [Fact]
    public void CalculateInYearOverseasSitesChargePence_NegativeCount_Throws()
    {
        var act = () => AccreditationChargeCalculator.CalculateInYearOverseasSitesChargePence(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void LockedFields_AreTheFourTicketFieldsTheOrsAlreadyHolds()
    {
        ChangeRequestRules.LockedFields(Site()).Should().BeEmpty();
        ChangeRequestRules
            .LockedFields(Site("India", "13.0827, 80.2707", ["R3"], false))
            .Should()
            .Equal("country", "coordinates", "operationCodes", "conditionsOfExport");
        ChangeRequestRules.LockedFields(Site(country: "  ")).Should().BeEmpty();
        ChangeRequestRules.LockedFields(null).Should().BeEmpty();
    }

    [Fact]
    public void Effective_PrefersTheOrsValueOverAnAnswer()
    {
        var effective = ChangeRequestRules.Effective(
            Site(country: "India"),
            new ChangeRequestSiteAnswers { Country = "Nepal", Coordinates = "1.0000, 2.0000" }
        );

        effective.Country.Should().Be("India");
        effective.Coordinates.Should().Be("1.0000, 2.0000");
    }

    [Theory]
    [InlineData(false, false, null, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, null, false)]
    [InlineData(false, true, null, false)]
    public void BesEvidence_RequiredOutsideEuAndOecdUnlessConditionsOfExportMet(
        bool isEu,
        bool isOecd,
        bool? conditionsOfExport,
        bool required
    )
    {
        var site = Site(isEu: isEu, isOecd: isOecd);

        ChangeRequestRules
            .IsBesEvidenceRequired(site, new ChangeRequestSiteAnswers { ConditionsOfExport = conditionsOfExport })
            .Should()
            .Be(required);
    }

    [Fact]
    public void MissingFields_BesEvidenceNeedsEveryFileClean()
    {
        var site = Site("India", "13.0827, 80.2707", ["R3"], false);
        var requestSite = new ChangeRequestSite
        {
            SiteId = 1,
            SiteName = "Site",
            BesEvidence = new BesEvidenceModel
            {
                BesEvidenceUploads =
                [
                    new BesEvidenceFileModel { FileId = "a", Filename = "a.pdf", S3Key = "a", ScanStatus = "Clean" },
                    new BesEvidenceFileModel { FileId = "b", Filename = "b.pdf", S3Key = "b", ScanStatus = "Infected" },
                ],
            },
        };

        ChangeRequestRules.MissingFields(site, requestSite).Should().Equal("besEvidence");
        requestSite.BesEvidence.BesEvidenceUploads.RemoveAt(1);
        ChangeRequestRules.MissingFields(site, requestSite).Should().BeEmpty();
    }
}
