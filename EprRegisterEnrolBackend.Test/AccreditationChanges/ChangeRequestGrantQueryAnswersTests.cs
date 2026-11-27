using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.DomainModel.Entities;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

/// <summary>
/// RA-698: the answers an operator gave to a query are applied on grant to the legacy site and to the ORS (and their
/// interim sites), and only what was answered is changed.
/// </summary>
public class ChangeRequestGrantQueryAnswersTests
{
    private static OverseasSiteModel LegacySite() =>
        new()
        {
            SiteId = 1,
            OrsId = "002",
            SiteName = "Old name",
            AddressLine1 = "1 Rue",
            AddressLine2 = "Old line 2",
            TownOrCity = "Paris",
            Country = "France",
            ContactName = "Old Contact",
            ContactEmail = "old@example.com",
            ContactPhone = "0100",
            RepatriatedLoads = "Old arrangements",
            InterimSites =
            [
                new InterimSiteModel
                {
                    SiteId = 7,
                    SiteNumber = "003",
                    Country = "France",
                    SiteName = "Old interim",
                    AddressLine1 = "2 Rue",
                    TownOrCity = "Lyon",
                    ContactName = "Interim Contact",
                    ContactEmail = "interim@example.com",
                    ContactPhone = "0200",
                },
            ],
        };

    private static ChangeRequestSiteAnswers AllAnswers() =>
        new()
        {
            SiteName = "New name",
            AddressLine1 = "10 Rue",
            AddressLine2 = "New line 2",
            TownOrCity = "Nice",
            StateOrRegion = "PACA",
            Postcode = "06000",
            ContactName = "New Contact",
            ContactEmail = "new@example.com",
            ContactPhone = "0300",
            RejectedLoadsArrangements = "New arrangements",
            InterimSites =
            [
                new ChangeRequestInterimSiteAnswers
                {
                    SiteId = 7,
                    SiteName = "New interim",
                    AddressLine1 = "20 Rue",
                    AddressLine2 = "Interim line 2",
                    TownOrCity = "Marseille",
                    StateOrRegion = "Provence",
                    Postcode = "13000",
                    ContactName = "New Interim Contact",
                    ContactEmail = "newinterim@example.com",
                    ContactPhone = "0400",
                },
                new ChangeRequestInterimSiteAnswers { SiteId = 99, SiteName = "Not on the site" },
            ],
        };

    [Fact]
    public void ApplyToLegacySite_WritesEveryAnsweredValue()
    {
        var site = LegacySite();

        ChangeRequestGrant.ApplySuppliedAnswers(site, AllAnswers());

        site.SiteName.Should().Be("New name");
        site.AddressLine1.Should().Be("10 Rue");
        site.AddressLine2.Should().Be("New line 2");
        site.TownOrCity.Should().Be("Nice");
        site.ContactName.Should().Be("New Contact");
        site.ContactEmail.Should().Be("new@example.com");
        site.ContactPhone.Should().Be("0300");
        site.RepatriatedLoads.Should().Be("New arrangements");
        var interim = site.InterimSites.Single();
        interim.SiteName.Should().Be("New interim");
        interim.AddressLine1.Should().Be("20 Rue");
        interim.AddressLine2.Should().Be("Interim line 2");
        interim.TownOrCity.Should().Be("Marseille");
        interim.StateOrRegion.Should().Be("Provence");
        interim.Postcode.Should().Be("13000");
        interim.ContactName.Should().Be("New Interim Contact");
        interim.ContactEmail.Should().Be("newinterim@example.com");
        interim.ContactPhone.Should().Be("0400");
    }

    private static OverseasSiteModel PreRa603Site()
    {
        var site = LegacySite();
        site.InterimSite = site.InterimSites.Single();
        site.InterimSites = [];
        return site;
    }

    [Fact]
    public void ApplyToLegacySite_PreRa603SingleInterimSite_IsKeptWhenNothingIsAnswered()
    {
        var site = PreRa603Site();

        ChangeRequestGrant.ApplySuppliedAnswers(site, new ChangeRequestSiteAnswers());

        site.InterimSite.Should().NotBeNull();
        site.InterimSite!.SiteName.Should().Be("Old interim");
        site.InterimSites.Should().ContainSingle();
    }

    [Fact]
    public void ApplyToLegacySite_PreRa603SingleInterimSite_TakesTheQueriedEdits()
    {
        var site = PreRa603Site();

        ChangeRequestGrant.ApplySuppliedAnswers(site, AllAnswers());

        site.InterimSites.Single().SiteName.Should().Be("New interim");
        site.InterimSite.Should().BeSameAs(site.InterimSites.Single());
    }

    [Fact]
    public void ApplyToLegacySite_NothingAnswered_LeavesTheSiteAsItWas()
    {
        var site = LegacySite();

        ChangeRequestGrant.ApplySuppliedAnswers(site, new ChangeRequestSiteAnswers());

        site.SiteName.Should().Be("Old name");
        site.AddressLine2.Should().Be("Old line 2");
        site.ContactEmail.Should().Be("old@example.com");
        site.RepatriatedLoads.Should().Be("Old arrangements");
        site.InterimSites.Single().SiteName.Should().Be("Old interim");
    }

    [Fact]
    public void ApplyToOrs_CreatesAddressAndContactAndWritesEveryAnsweredValue()
    {
        var site = LegacySite();
        var answers = AllAnswers();
        ChangeRequestGrant.ApplySuppliedAnswers(site, answers);
        var ors = new OverseasSiteEntity
        {
            InterimSites =
            [
                new InterimSiteEntry { SiteNumber = "003" },
                new InterimSiteEntry { SiteNumber = "004", SiteName = "Other" },
            ],
        };

        ChangeRequestGrant.ApplySuppliedAnswers(ors, answers, site);

        ors.SiteName.Should().Be("New name");
        ors.Address.Should()
            .BeEquivalentTo(
                new PostalAddress
                {
                    Line1 = "10 Rue",
                    Line2 = "New line 2",
                    TownOrCity = "Nice",
                    Region = "PACA",
                    Postcode = "06000",
                }
            );
        ors.Contact.Should()
            .BeEquivalentTo(new ContactPerson { FullName = "New Contact", Email = "new@example.com", Phone = "0300" });
        ors.RepatriatedLoads.Should().Be("New arrangements");
        var entry = ors.InterimSites.Single(e => e.SiteNumber == "003");
        entry.SiteName.Should().Be("New interim");
        entry.Address!.Line1.Should().Be("20 Rue");
        entry.Address.Line2.Should().Be("Interim line 2");
        entry.Address.TownOrCity.Should().Be("Marseille");
        entry.Address.Region.Should().Be("Provence");
        entry.Address.Postcode.Should().Be("13000");
        entry.Contact!.FullName.Should().Be("New Interim Contact");
        entry.Contact.Email.Should().Be("newinterim@example.com");
        entry.Contact.Phone.Should().Be("0400");
        ors.InterimSites.Single(e => e.SiteNumber == "004").SiteName.Should().Be("Other");
    }

    [Fact]
    public void ApplyToOrs_OnlyAnsweredValues_KeepsTheOrsOtherDetails()
    {
        var site = LegacySite();
        var answers = new ChangeRequestSiteAnswers { TownOrCity = "Nice", ContactPhone = "0300" };
        ChangeRequestGrant.ApplySuppliedAnswers(site, answers);
        var ors = new OverseasSiteEntity
        {
            SiteName = "Registered name",
            Address = new PostalAddress { Line1 = "Registered line", TownOrCity = "Paris" },
            Contact = new ContactPerson { FullName = "Registered Contact", Phone = "0100" },
            RepatriatedLoads = "Registered arrangements",
        };

        ChangeRequestGrant.ApplySuppliedAnswers(ors, answers, site);

        ors.SiteName.Should().Be("Registered name");
        ors.Address!.Line1.Should().Be("Registered line");
        ors.Address.TownOrCity.Should().Be("Nice");
        ors.Contact!.FullName.Should().Be("Registered Contact");
        ors.Contact.Phone.Should().Be("0300");
        ors.RepatriatedLoads.Should().Be("Registered arrangements");
    }

    [Fact]
    public void ApplyToOrs_NothingAnswered_CreatesNoAddressOrContact()
    {
        var ors = new OverseasSiteEntity();

        ChangeRequestGrant.ApplySuppliedAnswers(ors, new ChangeRequestSiteAnswers(), LegacySite());

        ors.Address.Should().BeNull();
        ors.Contact.Should().BeNull();
        ors.SiteName.Should().BeNull();
    }

    [Fact]
    public void ApplyToOrs_InterimSiteAnswerWithoutAnEntry_IsSkipped()
    {
        var site = LegacySite();
        var answers = new ChangeRequestSiteAnswers
        {
            InterimSites = [new ChangeRequestInterimSiteAnswers { SiteId = 7, SiteName = "x" }],
        };
        var ors = new OverseasSiteEntity();

        var act = () => ChangeRequestGrant.ApplySuppliedAnswers(ors, answers, site);

        act.Should().NotThrow();
        ors.InterimSites.Should().BeEmpty();
    }
}
