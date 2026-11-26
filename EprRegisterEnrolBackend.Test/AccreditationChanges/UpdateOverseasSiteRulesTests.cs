using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.DevSeed;
using FluentAssertions;
using FluentValidation.Results;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

/// <summary>RA-682: the ORS update rules in <see cref="ChangeRequestRules"/>, without HTTP.</summary>
public sealed class UpdateOverseasSiteRulesTests
{
    private static readonly AccreditationApplicationModel Accreditation =
        DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, 2026);

    private static OverseasSiteModel Ors(int siteId) => ChangeRequestRules.FindSite(Accreditation, siteId)!;

    private static ChangeRequestSite Update(int siteId)
    {
        var details = ChangeRequestRules.DetailsOf(Ors(siteId));
        return new ChangeRequestSite
        {
            ChangeType = AccreditationChangeRequest.UpdateOverseasSiteChangeType,
            SiteId = siteId,
            OrsId = Ors(siteId).OrsId!,
            SiteName = details.SiteName ?? "",
            Previous = details,
            Proposed = details,
        };
    }

    private static ChangeRequestSite Add(int siteId) =>
        new() { SiteId = siteId, OrsId = Ors(siteId).OrsId!, SiteName = Ors(siteId).SiteName ?? "" };

    private static AccreditationChangeRequest Request(string changeType, string? applicationId, params ChangeRequestSite[] sites) =>
        new()
        {
            OrganisationId = Accreditation.OrganisationId,
            ApplicationId = applicationId,
            RegistrationId = Accreditation.RegistrationId,
            MaterialType = Accreditation.MaterialType,
            Year = 2026,
            ChangeType = changeType,
            Sites = [.. sites],
        };

    private const string AddType = AccreditationChangeRequest.AddOverseasSiteChangeType;
    private const string UpdateType = AccreditationChangeRequest.UpdateOverseasSiteChangeType;

    [Fact]
    public void RequestChangeType_follows_the_items_of_an_accreditation_request()
    {
        var app = Accreditation.ApplicationId;
        ChangeRequestRules.RequestChangeType(Request(AddType, app, Update(902501))).Should().Be(UpdateType);
        ChangeRequestRules.RequestChangeType(Request(UpdateType, app, Update(902501), Add(902504))).Should().Be(AddType);
        ChangeRequestRules.RequestChangeType(Request(AddType, app, Add(902504))).Should().Be(AddType);
    }

    [Fact]
    public void RequestChangeType_keeps_a_registration_only_or_siteless_requests_own_type()
    {
        ChangeRequestRules.RequestChangeType(Request(UpdateType, null, Add(902504))).Should().Be(UpdateType);
        ChangeRequestRules
            .RequestChangeType(Request(AccreditationChangeRequest.UpgradeTonnageBandChangeType, Accreditation.ApplicationId))
            .Should()
            .Be(AccreditationChangeRequest.UpgradeTonnageBandChangeType);
    }

    [Fact]
    public void SubmissionGroups_send_adds_first()
    {
        var groups = ChangeRequestRules.SubmissionGroups(
            Request(AddType, Accreditation.ApplicationId, Update(902501), Add(902504), Update(902502))
        );

        groups.Select(g => g.Key).Should().Equal(AddType, UpdateType);
        groups[1].Select(s => s.SiteId).Should().Equal(902501, 902502);
    }

    [Fact]
    public void Rebasing_keeps_the_operators_value_and_takes_the_ORSs_for_the_rest()
    {
        var item = Update(902501);
        item.Proposed = item.Proposed! with { SiteName = "Mine", ContactName = "Mine too" };
        var now = Clone(Ors(902501));
        now.SiteName = "Renamed by support";
        now.ContactName = "Also changed";
        now.ContactEmail = "changed@example.com";

        ChangeRequestRules.RebaseOnCurrent(item, now);

        item.Previous!.SiteName.Should().Be("Renamed by support");
        item.Proposed!.SiteName.Should().Be("Mine");
        item.Proposed.ContactName.Should().Be("Mine too");
        item.Proposed.ContactEmail.Should().Be("changed@example.com");
    }

    [Fact]
    public void Rebasing_an_interim_change_follows_the_interim_site_and_drops_one_that_no_longer_changes_anything()
    {
        var item = Update(902501);
        var gdynia = ChangeRequestRules.ActiveInterimSite(Ors(902501), 902511)!;
        var szczecin = ChangeRequestRules.ActiveInterimSite(Ors(902501), 902512)!;
        ChangeRequestRules.SetInterimChange(item, gdynia, InterimSiteDetails.From(gdynia) with { SiteName = "Mine" });
        ChangeRequestRules.SetInterimChange(
            item,
            szczecin,
            InterimSiteDetails.From(szczecin) with { ContactName = "Pat Lee" }
        );
        var now = Clone(Ors(902501));
        now.InterimSites!.Single(i => i.SiteId == 902511).ContactName = "Changed";
        now.InterimSites!.Single(i => i.SiteId == 902512).ContactName = "Pat Lee";

        ChangeRequestRules.RebaseOnCurrent(item, now);

        var change = item.InterimSiteChanges.Should().ContainSingle().Subject;
        change.InterimSiteId.Should().Be(902511);
        change.Proposed!.SiteName.Should().Be("Mine");
        change.Proposed.ContactName.Should().Be("Changed");
    }

    [Fact]
    public void An_interim_change_keeps_the_interim_sites_own_country_and_codes()
    {
        var item = Update(902501);
        var gdynia = ChangeRequestRules.ActiveInterimSite(Ors(902501), 902511)!;

        ChangeRequestRules.SetInterimChange(
            item,
            gdynia,
            InterimSiteDetails.From(gdynia) with { Country = " Poland ", OperationCodes = [.. Enumerable.Reverse(gdynia.OperationCodes)] }
        );

        item.InterimSiteChanges.Should().BeEmpty();
    }

    [Fact]
    public void Locked_field_errors_are_left_out_including_a_lists_element_errors()
    {
        var validation = new ValidationResult(
            [
                new ValidationFailure("OperationCodes[0]", "bad code"),
                new ValidationFailure("Country", "required"),
                new ValidationFailure("SiteName", "required"),
            ]
        );

        ChangeRequestRules.WithoutLockedInterimFieldErrors(validation).Keys.Should().Equal("SiteName");
        ChangeRequestRules
            .WithoutLockedOrsFieldErrors(new ValidationResult([new ValidationFailure("coordinates", "bad")]))
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void An_update_whose_ORS_is_no_longer_accredited_is_missing_its_site()
    {
        ChangeRequestRules.MissingFields(null, Update(902501)).Should().Equal(ChangeRequestRules.Site);
    }

    // The seed builds a fresh model each time, so this is a copy the test can change.
    private static OverseasSiteModel Clone(OverseasSiteModel site) =>
        ChangeRequestRules.FindSite(
            DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, 2026),
            site.SiteId
        )!;
}
