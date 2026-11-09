using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Migration;

public class LegacySiteMapperTests
{
    private static readonly ObjectId RegistrationId = ObjectId.GenerateNewId();

    private static OverseasSiteModel Site() =>
        new()
        {
            SiteId = 1,
            OrsId = "001",
            SiteName = "Site One",
            SiteAddress = "1 Rue, Paris, France",
            AddressLine1 = "1 Rue",
            TownOrCity = "Paris",
            Country = "France",
            IsOecd = true,
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            OperationCodes = ["R3"],
            Code1 = "B3011",
            Code3 = "B3020",
            RepatriatedLoads = "returned",
            ConditionsOfExport = true,
        };

    [Fact]
    public void NewOverseasSite_CopiesTheSitesDetails()
    {
        var site = LegacySiteMapper.NewOverseasSite(RegistrationId, Site());

        Assert.Equal(RegistrationId, site.RegistrationId);
        Assert.Equal("001", site.OrsId);
        Assert.Equal("Site One", site.SiteName);
        Assert.Equal("1 Rue, Paris, France", site.FormattedAddress);
        Assert.Equal("Paris", site.Address!.TownOrCity);
        Assert.True(site.IsOecd);
        Assert.Equal("contact@example.com", site.Contact!.Email);
        Assert.Equal(["R3"], site.OperationCodes);
        Assert.Equal(["B3011", "B3020"], site.BaselOecdCodes);
        Assert.Equal("returned", site.RepatriatedLoads);
        Assert.True(site.ConditionsOfExport);
        Assert.Empty(site.InterimSites);
    }

    [Fact]
    public void NewOverseasSite_NoContactDetails_HasNoContact()
    {
        var legacy = Site();
        legacy.ContactName = null;
        legacy.ContactEmail = " ";

        Assert.Null(LegacySiteMapper.NewOverseasSite(RegistrationId, legacy).Contact);
    }

    [Fact]
    public void ToInterimSite_UsesTheGivenNumberAndKeepsItsDates()
    {
        var removed = new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var interim = new InterimSiteModel
        {
            SiteId = 2,
            SiteNumber = "SN-0001",
            Country = "Belgium",
            SiteName = "Interim",
            AddressLine1 = "1 Quai",
            TownOrCity = "Antwerp",
            StateOrRegion = "Flanders",
            Postcode = "2000",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            ContactPhone = "000",
            RemovedAt = removed,
        };

        var entry = LegacySiteMapper.ToInterimSite(interim, "004");

        Assert.Equal("004", entry.SiteNumber);
        Assert.Equal("Flanders", entry.Address!.Region);
        Assert.Equal("2000", entry.Address.Postcode);
        Assert.Equal("000", entry.Contact!.Phone);
        Assert.Null(entry.CreatedAt);
        Assert.Equal(removed, entry.RemovedAt);
    }

    [Fact]
    public void ProposedChanges_SameDetails_IsNull()
    {
        var ors = LegacySiteMapper.NewOverseasSite(RegistrationId, Site());
        ors.Status = OverseasSiteStatus.Active;

        Assert.Null(LegacySiteMapper.ProposedChanges(ors, Site()));
    }

    [Fact]
    public void ProposedChanges_HoldsOnlyTheFieldsThatDiffer()
    {
        var ors = LegacySiteMapper.NewOverseasSite(RegistrationId, Site());
        var edited = Site();
        edited.SiteName = "Renamed";
        edited.Code2 = "B3030";

        var changes = LegacySiteMapper.ProposedChanges(ors, edited)!;

        Assert.Equal(["siteName", "baselOecdCodes"], changes.Names);
        Assert.Equal("Renamed", changes["siteName"].AsString);
        Assert.Equal(
            ["B3011", "B3030", "B3020"],
            changes["baselOecdCodes"].AsBsonArray.Select(v => v.AsString)
        );
    }

    [Theory]
    [InlineData("001", true)]
    [InlineData("999", true)]
    [InlineData("SN-0001", false)]
    [InlineData("01", false)]
    [InlineData("0001", false)]
    [InlineData("0a1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsThreeDigitNumber_AcceptsOnlyThreeDigits(string? siteNumber, bool expected) =>
        Assert.Equal(expected, LegacySiteMapper.IsThreeDigitNumber(siteNumber));
}
