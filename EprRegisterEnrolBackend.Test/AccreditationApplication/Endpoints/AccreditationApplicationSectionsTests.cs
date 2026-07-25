using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationSectionsTests
{
    // --- IsSectionEditable ---

    [Theory]
    [InlineData(ApplicationStatus.Saved, SectionStatus.NotStarted, true)]
    [InlineData(ApplicationStatus.Started, SectionStatus.InProgress, true)]
    [InlineData(ApplicationStatus.Submitted, SectionStatus.Completed, true)]
    [InlineData(ApplicationStatus.Approved, SectionStatus.Completed, true)]
    [InlineData(ApplicationStatus.Rejected, SectionStatus.Completed, true)]
    [InlineData(ApplicationStatus.Updated, SectionStatus.Completed, true)]
    [InlineData(ApplicationStatus.Queried, SectionStatus.Queried, true)]
    [InlineData(ApplicationStatus.Queried, SectionStatus.NotStarted, false)]
    [InlineData(ApplicationStatus.Queried, SectionStatus.Completed, false)]
    public void IsSectionEditable_ReturnsExpected(
        ApplicationStatus appStatus,
        SectionStatus sectionStatus,
        bool expected
    )
    {
        AccreditationApplicationSections
            .IsSectionEditable(appStatus, sectionStatus)
            .Should()
            .Be(expected);
    }

    // --- CM key <-> operator section mapping ---

    [Theory]
    [InlineData("authority-to-issue", OperatorSection.Prns)]
    [InlineData("prn-tonnage", OperatorSection.Prns)]
    [InlineData("business-plan", OperatorSection.BusinessPlan)]
    [InlineData("sampling-and-inspection-plan", OperatorSection.SamplingPlan)]
    [InlineData("broadly-equivalent-standards", OperatorSection.BesEvidence)]
    [InlineData("overseas-reprocessing-sites", OperatorSection.OverseasSites)]
    public void TryMapCmKeyToSection_KnownKey_MapsToExpectedSection(
        string key,
        OperatorSection expected
    )
    {
        AccreditationApplicationSections.TryMapCmKeyToSection(key, out var section).Should().BeTrue();
        section.Should().Be(expected);
    }

    [Fact]
    public void TryMapCmKeyToSection_UnknownKey_ReturnsFalse()
    {
        AccreditationApplicationSections
            .TryMapCmKeyToSection("not-a-real-key", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void CmSectionKeysFor_Prns_ReturnsBothCollapsedKeys()
    {
        AccreditationApplicationSections
            .CmSectionKeysFor(OperatorSection.Prns)
            .Should()
            .BeEquivalentTo(["authority-to-issue", "prn-tonnage"]);
    }

    [Fact]
    public void ExporterOnlyCmSectionKeys_IsBesEvidenceAndOverseasSitesOnly()
    {
        // authority-to-issue/prn-tonnage (Prns) apply to every application; only BES
        // evidence/overseas sites are exporter-specific (OverseasSites is only ever created
        // when IsExporter is true — see AccreditationApplicationEndpoints.Seed).
        AccreditationApplicationSections
            .ExporterOnlyCmSectionKeys.Should()
            .BeEquivalentTo(["broadly-equivalent-standards", "overseas-reprocessing-sites"]);
    }

    // --- Get/SetSectionStatus ---

    [Fact]
    public void GetSectionStatus_OverseasSitesNull_ReturnsNotStarted()
    {
        var application = CreateApplication();
        AccreditationApplicationSections
            .GetSectionStatus(application, OperatorSection.OverseasSites)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void SetSectionStatus_OverseasSitesNull_CreatesSectionAndSetsStatus()
    {
        var application = CreateApplication();
        AccreditationApplicationSections.SetSectionStatus(
            application,
            OperatorSection.OverseasSites,
            SectionStatus.Queried
        );

        application.OverseasSites.Should().NotBeNull();
        application.OverseasSites!.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    [Fact]
    public void SetSectionStatus_Prns_SetsPrnsSectionStatus()
    {
        var application = CreateApplication();
        AccreditationApplicationSections.SetSectionStatus(
            application,
            OperatorSection.Prns,
            SectionStatus.Queried
        );
        application.Prns.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    // --- ComputeCurrentStatus ---

    [Fact]
    public void ComputeCurrentStatus_Prns_MatchesSectionStatusService()
    {
        var application = CreateApplication();
        application.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo500;
        application.Prns.Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "j@x.com" }];

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, OperatorSection.Prns)
            .Should()
            .Be(SectionStatus.Completed);
    }

    [Fact]
    public void ComputeCurrentStatus_OverseasSitesWithSelectedSite_IsCompleted()
    {
        var application = CreateApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site 1", Selected = true }],
        };

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, OperatorSection.OverseasSites)
            .Should()
            .Be(SectionStatus.Completed);
    }

    [Fact]
    public void ComputeCurrentStatus_BesEvidence_IsAlwaysNotStarted()
    {
        var application = CreateApplication();
        application.BesEvidence = new AccreditationApplicationBesEvidence
        {
            SectionStatus = SectionStatus.Queried,
        };

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, OperatorSection.BesEvidence)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    // --- SnapshotSection ---

    [Fact]
    public void SnapshotSection_Prns_AppendsCurrentValues()
    {
        var application = CreateApplication();
        application.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo1000;
        application.Prns.Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "j@x.com" }];
        var versionedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        AccreditationApplicationSections.SnapshotSection(application, OperatorSection.Prns, versionedAt);

        application.Prns.Versions.Should().ContainSingle();
        var snapshot = application.Prns.Versions[0];
        snapshot.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo1000);
        snapshot.Authorisers.Should().ContainSingle(a => a.FullName == "Jane");
        snapshot.VersionedAt.Should().Be(versionedAt);
    }

    [Fact]
    public void SnapshotSection_CalledTwice_AppendsRatherThanOverwrites()
    {
        var application = CreateApplication();

        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.BusinessPlan,
            DateTime.UtcNow
        );
        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.BusinessPlan,
            DateTime.UtcNow
        );

        application.BusinessPlan.Versions.Should().HaveCount(2);
    }

    [Fact]
    public void SnapshotSection_OverseasSitesNull_CreatesSectionAndAppendsSnapshot()
    {
        var application = CreateApplication();

        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.OverseasSites,
            DateTime.UtcNow
        );

        application.OverseasSites.Should().NotBeNull();
        application.OverseasSites!.Versions.Should().ContainSingle();
    }

    private static AccreditationApplicationModel CreateApplication() =>
        new()
        {
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Plastic,
        };
}
