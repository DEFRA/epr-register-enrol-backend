using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

public class AccreditationApplicationSectionsTests
{
    // --- IsSectionEditable ---

    [Theory]
    [InlineData(ApplicationStatus.Saved, SectionStatus.NotStarted, true)]
    [InlineData(ApplicationStatus.Started, SectionStatus.InProgress, true)]
    [InlineData(ApplicationStatus.Approved, SectionStatus.Completed, true)]
    [InlineData(ApplicationStatus.Rejected, SectionStatus.Completed, true)]
    [InlineData(ApplicationStatus.Queried, SectionStatus.Queried, true)]
    [InlineData(ApplicationStatus.Queried, SectionStatus.NotStarted, false)]
    [InlineData(ApplicationStatus.Queried, SectionStatus.Completed, false)]
    // RA-481: Submitted/DulyMade/Updated/AwaitingDecision are "locked" statuses — editable only
    // when the section itself is still Queried, same as the Queried application status above.
    [InlineData(ApplicationStatus.Submitted, SectionStatus.Completed, false)]
    [InlineData(ApplicationStatus.Submitted, SectionStatus.Queried, true)]
    [InlineData(ApplicationStatus.DulyMade, SectionStatus.Completed, false)]
    [InlineData(ApplicationStatus.DulyMade, SectionStatus.Queried, true)]
    [InlineData(ApplicationStatus.Updated, SectionStatus.Completed, false)]
    [InlineData(ApplicationStatus.Updated, SectionStatus.Queried, true)]
    [InlineData(ApplicationStatus.AwaitingDecision, SectionStatus.Completed, false)]
    [InlineData(ApplicationStatus.AwaitingDecision, SectionStatus.Queried, true)]
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

    // --- Case Management service key <-> operator section mapping ---

    [Theory]
    [InlineData("authority-to-issue", OperatorSection.Prns)]
    [InlineData("prn-tonnage", OperatorSection.Prns)]
    [InlineData("business-plan", OperatorSection.BusinessPlan)]
    [InlineData("sampling-and-inspection-plan", OperatorSection.SamplingPlan)]
    [InlineData("broadly-equivalent-standards", OperatorSection.BesEvidence)]
    [InlineData("overseas-reprocessing-sites", OperatorSection.OverseasSites)]
    public void TryMapCaseManagementKeyToSection_KnownKey_MapsToExpectedSection(
        string key,
        OperatorSection expected
    )
    {
        AccreditationApplicationSections.TryMapCaseManagementKeyToSection(key, out var section).Should().BeTrue();
        section.Should().Be(expected);
    }

    [Fact]
    public void TryMapCaseManagementKeyToSection_UnknownKey_ReturnsFalse()
    {
        AccreditationApplicationSections
            .TryMapCaseManagementKeyToSection("not-a-real-key", out _)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void CaseManagementSectionKeysFor_Prns_ReturnsBothCollapsedKeys()
    {
        AccreditationApplicationSections
            .CaseManagementSectionKeysFor(OperatorSection.Prns)
            .Should()
            .BeEquivalentTo(["authority-to-issue", "prn-tonnage"]);
    }

    [Fact]
    public void ExporterOnlyCaseManagementSectionKeys_IsBesEvidenceAndOverseasSitesOnly()
    {
        // authority-to-issue/prn-tonnage (Prns) apply to every application; only BES
        // evidence/overseas sites are exporter-specific (OverseasSites is only ever created
        // when IsExporter is true — see AccreditationApplicationEndpoints.Seed).
        AccreditationApplicationSections
            .ExporterOnlyCaseManagementSectionKeys.Should()
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
        application.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo5000;
        application.Prns.Authorisers = [new PrnsAuthoriser { FullName = "Jane", Email = "j@x.com" }];
        var versionedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        AccreditationApplicationSections.SnapshotSection(application, OperatorSection.Prns, versionedAt);

        application.Prns.Versions.Should().ContainSingle();
        var snapshot = application.Prns.Versions[0];
        snapshot.PlannedTonnageBand.Should().Be(PlannedTonnageBand.UpTo5000);
        snapshot.Authorisers.Should().ContainSingle(a => a.FullName == "Jane");
        snapshot.VersionedAt.Should().Be(versionedAt);
    }

    [Fact]
    public void SnapshotSection_BusinessPlan_AppendsCurrentValuesIncludingOther()
    {
        // RA-456: Other must be carried into the snapshot alongside the original six fields.
        var application = CreateApplication();
        application.BusinessPlan.NewInfrastructurePercent = 20;
        application.BusinessPlan.OtherPercent = 10;
        application.BusinessPlan.OtherDetail = "Other spend detail";
        var versionedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.BusinessPlan,
            versionedAt
        );

        application.BusinessPlan.Versions.Should().ContainSingle();
        var snapshot = application.BusinessPlan.Versions[0];
        snapshot.NewInfrastructurePercent.Should().Be(20);
        snapshot.OtherPercent.Should().Be(10);
        snapshot.OtherDetail.Should().Be("Other spend detail");
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

    // --- CaseManagementSectionKeysFor (remaining switch arms) ---

    [Fact]
    public void CaseManagementSectionKeysFor_BusinessPlan_ReturnsBusinessPlanKey()
    {
        AccreditationApplicationSections
            .CaseManagementSectionKeysFor(OperatorSection.BusinessPlan)
            .Should()
            .BeEquivalentTo(["business-plan"]);
    }

    [Fact]
    public void CaseManagementSectionKeysFor_SamplingPlan_ReturnsSamplingAndInspectionPlanKey()
    {
        AccreditationApplicationSections
            .CaseManagementSectionKeysFor(OperatorSection.SamplingPlan)
            .Should()
            .BeEquivalentTo(["sampling-and-inspection-plan"]);
    }

    [Fact]
    public void CaseManagementSectionKeysFor_BesEvidence_ReturnsBroadlyEquivalentStandardsKey()
    {
        AccreditationApplicationSections
            .CaseManagementSectionKeysFor(OperatorSection.BesEvidence)
            .Should()
            .BeEquivalentTo(["broadly-equivalent-standards"]);
    }

    [Fact]
    public void CaseManagementSectionKeysFor_OverseasSites_ReturnsOverseasReprocessingSitesKey()
    {
        AccreditationApplicationSections
            .CaseManagementSectionKeysFor(OperatorSection.OverseasSites)
            .Should()
            .BeEquivalentTo(["overseas-reprocessing-sites"]);
    }

    [Fact]
    public void CaseManagementSectionKeysFor_UnknownSection_ReturnsEmpty()
    {
        AccreditationApplicationSections
            .CaseManagementSectionKeysFor((OperatorSection)999)
            .Should()
            .BeEmpty();
    }

    // --- GetSectionStatus (remaining switch arms) ---

    [Fact]
    public void GetSectionStatus_Prns_ReturnsPrnsSectionStatus()
    {
        var application = CreateApplication();
        application.Prns.SectionStatus = SectionStatus.InProgress;

        AccreditationApplicationSections
            .GetSectionStatus(application, OperatorSection.Prns)
            .Should()
            .Be(SectionStatus.InProgress);
    }

    [Fact]
    public void GetSectionStatus_BusinessPlan_ReturnsBusinessPlanSectionStatus()
    {
        var application = CreateApplication();
        application.BusinessPlan.SectionStatus = SectionStatus.Completed;

        AccreditationApplicationSections
            .GetSectionStatus(application, OperatorSection.BusinessPlan)
            .Should()
            .Be(SectionStatus.Completed);
    }

    [Fact]
    public void GetSectionStatus_SamplingPlan_ReturnsSamplingPlanSectionStatus()
    {
        var application = CreateApplication();
        application.SamplingPlan.SectionStatus = SectionStatus.Queried;

        AccreditationApplicationSections
            .GetSectionStatus(application, OperatorSection.SamplingPlan)
            .Should()
            .Be(SectionStatus.Queried);
    }

    [Fact]
    public void GetSectionStatus_OverseasSitesNonNull_ReturnsItsSectionStatus()
    {
        var application = CreateApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            SectionStatus = SectionStatus.InProgress,
        };

        AccreditationApplicationSections
            .GetSectionStatus(application, OperatorSection.OverseasSites)
            .Should()
            .Be(SectionStatus.InProgress);
    }

    [Fact]
    public void GetSectionStatus_BesEvidenceNull_ReturnsNotStarted()
    {
        var application = CreateApplication();

        AccreditationApplicationSections
            .GetSectionStatus(application, OperatorSection.BesEvidence)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void GetSectionStatus_BesEvidenceNonNull_ReturnsItsSectionStatus()
    {
        var application = CreateApplication();
        application.BesEvidence = new AccreditationApplicationBesEvidence
        {
            SectionStatus = SectionStatus.Completed,
        };

        AccreditationApplicationSections
            .GetSectionStatus(application, OperatorSection.BesEvidence)
            .Should()
            .Be(SectionStatus.Completed);
    }

    [Fact]
    public void GetSectionStatus_UnknownSection_ReturnsNotStarted()
    {
        var application = CreateApplication();

        AccreditationApplicationSections
            .GetSectionStatus(application, (OperatorSection)999)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    // --- SetSectionStatus (remaining switch arms) ---

    [Fact]
    public void SetSectionStatus_BusinessPlan_SetsBusinessPlanSectionStatus()
    {
        var application = CreateApplication();
        AccreditationApplicationSections.SetSectionStatus(
            application,
            OperatorSection.BusinessPlan,
            SectionStatus.Completed
        );
        application.BusinessPlan.SectionStatus.Should().Be(SectionStatus.Completed);
    }

    [Fact]
    public void SetSectionStatus_SamplingPlan_SetsSamplingPlanSectionStatus()
    {
        var application = CreateApplication();
        AccreditationApplicationSections.SetSectionStatus(
            application,
            OperatorSection.SamplingPlan,
            SectionStatus.InProgress
        );
        application.SamplingPlan.SectionStatus.Should().Be(SectionStatus.InProgress);
    }

    [Fact]
    public void SetSectionStatus_BesEvidenceNull_CreatesSectionAndSetsStatus()
    {
        var application = CreateApplication();
        AccreditationApplicationSections.SetSectionStatus(
            application,
            OperatorSection.BesEvidence,
            SectionStatus.Queried
        );

        application.BesEvidence.Should().NotBeNull();
        application.BesEvidence!.SectionStatus.Should().Be(SectionStatus.Queried);
    }

    // --- ComputeCurrentStatus (remaining switch arms) ---

    [Fact]
    public void ComputeCurrentStatus_BusinessPlan_MatchesSectionStatusService()
    {
        var application = CreateApplication();
        application.BusinessPlan.NewInfrastructurePercent = 100;
        application.BusinessPlan.PriceSupportPercent = 0;
        application.BusinessPlan.BusinessCollectionsPercent = 0;
        application.BusinessPlan.CommunicationsPercent = 0;
        application.BusinessPlan.NewMarketsPercent = 0;
        application.BusinessPlan.NewUsesPercent = 0;
        application.BusinessPlan.OtherPercent = 0;

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, OperatorSection.BusinessPlan)
            .Should()
            .Be(SectionStatus.Completed);
    }

    [Fact]
    public void ComputeCurrentStatus_SamplingPlan_MatchesSectionStatusService()
    {
        var application = CreateApplication();

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, OperatorSection.SamplingPlan)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void ComputeCurrentStatus_OverseasSitesWithNoSelectedSite_IsNotStarted()
    {
        var application = CreateApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site 1", Selected = false }],
        };

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, OperatorSection.OverseasSites)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void ComputeCurrentStatus_OverseasSitesNull_IsNotStarted()
    {
        var application = CreateApplication();

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, OperatorSection.OverseasSites)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    [Fact]
    public void ComputeCurrentStatus_UnknownSection_ReturnsNotStarted()
    {
        var application = CreateApplication();

        AccreditationApplicationSections
            .ComputeCurrentStatus(application, (OperatorSection)999)
            .Should()
            .Be(SectionStatus.NotStarted);
    }

    // --- SnapshotSection (remaining switch arms) ---

    [Fact]
    public void SnapshotSection_SamplingPlan_AppendsCurrentValues()
    {
        var application = CreateApplication();
        var versionedAt = new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc);

        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.SamplingPlan,
            versionedAt
        );

        application.SamplingPlan.Versions.Should().ContainSingle();
        application.SamplingPlan.Versions[0].VersionedAt.Should().Be(versionedAt);
    }

    [Fact]
    public void SnapshotSection_BesEvidenceNull_CreatesSectionAndAppendsSnapshot()
    {
        var application = CreateApplication();
        var versionedAt = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        AccreditationApplicationSections.SnapshotSection(
            application,
            OperatorSection.BesEvidence,
            versionedAt
        );

        application.BesEvidence.Should().NotBeNull();
        application.BesEvidence!.Versions.Should().ContainSingle();
        application.BesEvidence.Versions[0].VersionedAt.Should().Be(versionedAt);
    }

    // --- BuildSectionStatusUpdate / BuildSnapshotUpdate ---
    //
    // RA-519: UpdateDefinition<T> has no public inspection API beyond Render, so these tests
    // render each definition to JSON (mirroring
    // AccreditationApplicationPersistenceTests.RenderFilter) and assert on that shape, rather
    // than trying to execute the update against a real/fake collection. Lower-cased throughout
    // (rather than FluentAssertions' ContainEquivalentOf) so a NotContain check reads as plainly
    // as a Contain one — whether the camelCase element-name convention is active depends on
    // MongoDbClientFactory having been constructed elsewhere in the same test run, which these
    // tests deliberately don't do (see RenderFilter's own comment on the same issue).
    private static string RenderUpdateAsLowerJson(UpdateDefinition<AccreditationApplicationModel> update)
    {
        var registry = BsonSerializer.SerializerRegistry;
        return update
            .Render(
                new RenderArgs<AccreditationApplicationModel>(
                    registry.GetSerializer<AccreditationApplicationModel>(),
                    registry
                )
            )
            .ToJson()
            .ToLowerInvariant();
    }

    [Fact]
    public void BuildSectionStatusUpdate_Prns_SetsPrnsSectionStatus()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            OperatorSection.Prns,
            SectionStatus.Queried
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("$set");
        // SectionStatus has no [BsonRepresentation(BsonType.String)], so it serializes as its
        // numeric ordinal — Queried is 4 (NotStarted, InProgress, Completed, Submitted, Queried).
        rendered.Should().Contain("prns.sectionstatus\" : 4");
    }

    [Fact]
    public void BuildSectionStatusUpdate_BusinessPlan_SetsBusinessPlanSectionStatus()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            OperatorSection.BusinessPlan,
            SectionStatus.Completed
        );

        var rendered = RenderUpdateAsLowerJson(update);
        // SectionStatus has no [BsonRepresentation(BsonType.String)], so it serializes as its
        // numeric ordinal — Completed is 2 (NotStarted, InProgress, Completed, Submitted, Queried).
        rendered.Should().Contain("businessplan.sectionstatus\" : 2");
    }

    // RA-519 review follow-up (tomhalley): BuildSnapshotUpdate's BusinessPlan case copies 14
    // fields by name into BusinessPlanSnapshot - a transposed or omitted field compiles cleanly
    // and would silently corrupt every resubmitted BusinessPlan snapshot. These two tests
    // deserialize the rendered $push payload back into the real snapshot type and assert full
    // structural equality against the values actually on the application, so a transposition
    // (e.g. OtherPercent's value landing in NewUsesPercent) fails the test even though every
    // field individually still "looks present" the way a substring Contains check would miss.
    [Fact]
    public void BuildSnapshotUpdate_Prns_PushesSnapshotWithEveryCurrentField()
    {
        var application = CreateApplication();
        application.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo10000;
        application.Prns.Authorisers =
        [
            new PrnsAuthoriser { FullName = "Jane", Email = "jane@example.com" },
            new PrnsAuthoriser { FullName = "Bob", Email = "bob@example.com" },
        ];
        var versionedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            OperatorSection.Prns,
            versionedAt
        );

        var pushed = ExtractPushedDocument(update, "prns.versions");
        var snapshot = BsonSerializer.Deserialize<PrnsSnapshot>(pushed);
        snapshot
            .Should()
            .BeEquivalentTo(
                new PrnsSnapshot
                {
                    PlannedTonnageBand = PlannedTonnageBand.UpTo10000,
                    Authorisers = application.Prns.Authorisers,
                    VersionedAt = versionedAt,
                }
            );
    }

    [Fact]
    public void BuildSnapshotUpdate_BusinessPlan_PushesSnapshotWithEveryCurrentField()
    {
        var application = CreateApplication();
        var bp = application.BusinessPlan;
        bp.NewInfrastructurePercent = 5;
        bp.PriceSupportPercent = 10;
        bp.BusinessCollectionsPercent = 15;
        bp.CommunicationsPercent = 20;
        bp.NewMarketsPercent = 25;
        bp.NewUsesPercent = 30;
        bp.OtherPercent = 35;
        bp.NewInfrastructureDetail = "infra detail";
        bp.PriceSupportDetail = "price detail";
        bp.BusinessCollectionsDetail = "collections detail";
        bp.CommunicationsDetail = "comms detail";
        bp.NewMarketsDetail = "markets detail";
        bp.NewUsesDetail = "uses detail";
        bp.OtherDetail = "other detail";
        var versionedAt = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            OperatorSection.BusinessPlan,
            versionedAt
        );

        var pushed = ExtractPushedDocument(update, "businessplan.versions");
        var snapshot = BsonSerializer.Deserialize<BusinessPlanSnapshot>(pushed);
        snapshot
            .Should()
            .BeEquivalentTo(
                new BusinessPlanSnapshot
                {
                    NewInfrastructurePercent = 5,
                    PriceSupportPercent = 10,
                    BusinessCollectionsPercent = 15,
                    CommunicationsPercent = 20,
                    NewMarketsPercent = 25,
                    NewUsesPercent = 30,
                    OtherPercent = 35,
                    NewInfrastructureDetail = "infra detail",
                    PriceSupportDetail = "price detail",
                    BusinessCollectionsDetail = "collections detail",
                    CommunicationsDetail = "comms detail",
                    NewMarketsDetail = "markets detail",
                    NewUsesDetail = "uses detail",
                    OtherDetail = "other detail",
                    VersionedAt = versionedAt,
                }
            );
    }

    // Renders `update`, locates its single $push payload at `dottedFieldNameLower` (case-
    // insensitively, since - as elsewhere in this file - whether the camelCase element-name
    // convention is active depends on test run order) and returns it as a standalone BsonDocument
    // ready to deserialize back into the real snapshot type.
    private static BsonDocument ExtractPushedDocument(
        UpdateDefinition<AccreditationApplicationModel> update,
        string dottedFieldNameLower
    )
    {
        var registry = BsonSerializer.SerializerRegistry;
        var rendered = update
            .Render(
                new RenderArgs<AccreditationApplicationModel>(
                    registry.GetSerializer<AccreditationApplicationModel>(),
                    registry
                )
            )
            .AsBsonDocument;
        var pushOps = rendered["$push"].AsBsonDocument;
        var matchingField = pushOps.Names.Single(n =>
            string.Equals(n, dottedFieldNameLower, StringComparison.OrdinalIgnoreCase)
        );
        return pushOps[matchingField].AsBsonDocument;
    }

    [Fact]
    public void BuildSectionStatusUpdate_SamplingPlan_SetsSamplingPlanSectionStatus()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            OperatorSection.SamplingPlan,
            SectionStatus.Queried
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("$set");
        // SectionStatus has no [BsonRepresentation(BsonType.String)], so it serializes as its
        // numeric ordinal — Queried is 4 (NotStarted, InProgress, Completed, Submitted, Queried).
        rendered.Should().Contain("samplingplan.sectionstatus\" : 4");
    }

    [Fact]
    public void BuildSectionStatusUpdate_OverseasSitesNull_SetsWholeSubdocument()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            OperatorSection.OverseasSites,
            SectionStatus.Queried
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("overseassites");
        rendered.Should().Contain("sectionstatus");
        rendered
            .Should()
            .NotContain(
                "overseassites.sectionstatus",
                "a null OverseasSites must set the whole subdocument, not a dotted path into a not-yet-existing parent"
            );
    }

    [Fact]
    public void BuildSectionStatusUpdate_OverseasSitesNonNull_SetsNestedSectionStatusOnly()
    {
        var application = CreateApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            OperatorSection.OverseasSites,
            SectionStatus.Completed
        );

        var rendered = RenderUpdateAsLowerJson(update);
        // SectionStatus has no [BsonRepresentation(BsonType.String)], so it serializes as its
        // numeric ordinal — Completed is 2 (NotStarted, InProgress, Completed, Submitted, Queried).
        rendered.Should().Contain("overseassites.sectionstatus\" : 2");
    }

    [Fact]
    public void BuildSectionStatusUpdate_BesEvidenceNull_SetsWholeSubdocument()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            OperatorSection.BesEvidence,
            SectionStatus.Queried
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("besevidence");
        rendered.Should().Contain("sectionstatus");
        rendered
            .Should()
            .NotContain(
                "besevidence.sectionstatus",
                "a null BesEvidence must set the whole subdocument, not a dotted path into a not-yet-existing parent"
            );
    }

    [Fact]
    public void BuildSectionStatusUpdate_BesEvidenceNonNull_SetsNestedSectionStatusOnly()
    {
        var application = CreateApplication();
        application.BesEvidence = new AccreditationApplicationBesEvidence();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            OperatorSection.BesEvidence,
            SectionStatus.Completed
        );

        var rendered = RenderUpdateAsLowerJson(update);
        // SectionStatus has no [BsonRepresentation(BsonType.String)], so it serializes as its
        // numeric ordinal — Completed is 2 (NotStarted, InProgress, Completed, Submitted, Queried).
        rendered.Should().Contain("besevidence.sectionstatus\" : 2");
    }

    [Fact]
    public void BuildSectionStatusUpdate_UnknownSection_RendersToEmptyUpdate()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSectionStatusUpdate(
            application,
            (OperatorSection)999,
            SectionStatus.Queried
        );

        update
            .Render(
                new RenderArgs<AccreditationApplicationModel>(
                    BsonSerializer.SerializerRegistry.GetSerializer<AccreditationApplicationModel>(),
                    BsonSerializer.SerializerRegistry
                )
            )
            .AsBsonDocument.ElementCount.Should()
            .Be(0);
    }

    [Fact]
    public void BuildSnapshotUpdate_SamplingPlan_PushesSnapshotWithCurrentFiles()
    {
        var application = CreateApplication();
        application.SamplingPlan.Files =
        [
            new AccreditationApplicationFile
            {
                FileId = "f1",
                Filename = "plan.pdf",
                ContentType = "application/pdf",
                UploadedByUserId = "user-1",
                S3Key = "s3-key-1",
            },
        ];

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            OperatorSection.SamplingPlan,
            DateTime.UtcNow
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("$push");
        rendered.Should().Contain("samplingplan.versions");
        rendered.Should().Contain("f1", "the current file should be captured in the snapshot");
    }

    [Fact]
    public void BuildSnapshotUpdate_OverseasSitesNull_SetsWholeSubdocumentWithSingleVersion()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            OperatorSection.OverseasSites,
            DateTime.UtcNow
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("overseassites");
        rendered.Should().Contain("versions");
        rendered
            .Should()
            .NotContain(
                "overseassites.versions",
                "a null OverseasSites must set the whole subdocument, not push onto a not-yet-existing parent"
            );
    }

    [Fact]
    public void BuildSnapshotUpdate_OverseasSitesNonNull_PushesSnapshotWithCurrentSites()
    {
        var application = CreateApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [new OverseasSiteModel { SiteId = 1, SiteName = "Site 1", Selected = true }],
        };

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            OperatorSection.OverseasSites,
            DateTime.UtcNow
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("$push");
        rendered.Should().Contain("overseassites.versions");
        rendered.Should().Contain("site 1", "the current sites should be captured in the snapshot");
    }

    [Fact]
    public void BuildSnapshotUpdate_BesEvidenceNull_SetsWholeSubdocumentWithSingleVersion()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            OperatorSection.BesEvidence,
            DateTime.UtcNow
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("besevidence");
        rendered.Should().Contain("versions");
        rendered
            .Should()
            .NotContain(
                "besevidence.versions",
                "a null BesEvidence must set the whole subdocument, not push onto a not-yet-existing parent"
            );
    }

    [Fact]
    public void BuildSnapshotUpdate_BesEvidenceNonNull_PushesSnapshot()
    {
        var application = CreateApplication();
        application.BesEvidence = new AccreditationApplicationBesEvidence();

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            OperatorSection.BesEvidence,
            DateTime.UtcNow
        );

        var rendered = RenderUpdateAsLowerJson(update);
        rendered.Should().Contain("$push");
        rendered.Should().Contain("besevidence.versions");
    }

    [Fact]
    public void BuildSnapshotUpdate_UnknownSection_RendersToEmptyUpdate()
    {
        var application = CreateApplication();

        var update = AccreditationApplicationSections.BuildSnapshotUpdate(
            application,
            (OperatorSection)999,
            DateTime.UtcNow
        );

        update
            .Render(
                new RenderArgs<AccreditationApplicationModel>(
                    BsonSerializer.SerializerRegistry.GetSerializer<AccreditationApplicationModel>(),
                    BsonSerializer.SerializerRegistry
                )
            )
            .AsBsonDocument.ElementCount.Should()
            .Be(0);
    }
}
