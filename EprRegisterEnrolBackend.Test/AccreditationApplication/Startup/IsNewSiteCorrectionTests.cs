using System.Diagnostics.Metrics;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.ReEx;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using RunResult = EprRegisterEnrolBackend.AccreditationApplication.Startup.IsNewSiteCorrectionMigration.RunResult;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// epr-2uxy: the classifier on its own.
public class IsNewSiteClassifierTests
{
    internal static OverseasSiteModel Site(
        int siteId,
        string name,
        bool isNewSite = true,
        string? orsId = null
    ) =>
        new()
        {
            SiteId = siteId,
            SiteName = name,
            IsNewSite = isNewSite,
            OrsId = orsId,
        };

    // What PromoteOverseasSite (ApplyPromotedFields) leaves on a ReEx site: the full operator
    // detail set, RegisteredNowAccredited, and no OrsId.
    internal static OverseasSiteModel PromotedWithFullDetail(int siteId, string name)
    {
        var site = Site(siteId, name);
        site.RegisteredNowAccredited = true;
        site.ContactName = "Greta Schmidt";
        site.ContactEmail = "greta@example.com";
        site.ContactPhone = "+49 89 0000";
        site.OperationCodes = ["R3"];
        site.Code1 = "A1181";
        site.AddressLine1 = "Neue Strasse 1";
        site.TownOrCity = "Munich";
        site.Coordinates = "48.1, 11.5";
        site.RepatriatedLoads = "None";
        site.ConditionsOfExport = true;
        site.PreviousSites = [Site(siteId, name)];
        return site;
    }

    private static ReExSiteLookup ReExHolds(params (int Id, string Name)[] sites) =>
        ReExSiteLookup.Found([.. sites.Select(s => Site(s.Id, s.Name, isNewSite: false))]);

    [Fact]
    public void NotFlaggedNew_IsLeftAlone() =>
        IsNewSiteClassifier
            .Classify(Site(1, "A", isNewSite: false), null)
            .Verdict.Should()
            .Be(IsNewSiteVerdict.NotFlaggedNew);

    [Fact]
    public void OperatorAddedWithOrsId_IsAlreadyCorrect_WithoutAskingReEx()
    {
        var site = PromotedWithFullDetail(7, "Operator Added GmbH");
        site.RegisteredNowAccredited = false;
        site.OrsId = "007";

        IsNewSiteClassifier
            .Classify(site, null)
            .Verdict.Should()
            .Be(IsNewSiteVerdict.AlreadyCorrect);
    }

    [Fact]
    public void NoOrsId_AndReExHoldsTheSite_IsProvablyCorrupt() =>
        IsNewSiteClassifier
            .Classify(Site(1, "Carried Over Ltd"), ReExHolds((1, " carried over ltd ")))
            .Should()
            .Be(new IsNewSiteClassification(IsNewSiteVerdict.ProvablyCorrupt, []));

    [Fact]
    public void PromotedReExSiteWithFullOperatorDetail_IsPromotedCorrectable() =>
        IsNewSiteClassifier
            .Classify(PromotedWithFullDetail(2, "Promoted SA"), ReExHolds((2, "Promoted SA")))
            .Verdict.Should()
            .Be(IsNewSiteVerdict.PromotedCorrectable);

    [Fact]
    public void ReExSiteRenamedByAnEdit_IsMatchedOnItsUndoStackName()
    {
        var site = Site(3, "New Name");
        site.ContactName = "Edited In";
        site.PreviousSites = [Site(3, "Old Name")];

        IsNewSiteClassifier
            .Classify(site, ReExHolds((3, "Old Name")))
            .Verdict.Should()
            .Be(IsNewSiteVerdict.ProvablyCorrupt);
    }

    [Fact]
    public void NoOrsId_AndReExDoesNotHoldIt_IsRefused_NamingWhy()
    {
        // The shape the PATCH hole could create: unknown id, no OrsId, no detail.
        var result = IsNewSiteClassifier.Classify(Site(99, "Smuggled"), ReExHolds((1, "Other")));

        result.Verdict.Should().Be(IsNewSiteVerdict.AmbiguousRefused);
        result.RefusedBecause.Should().Equal(IsNewSiteClassifier.NotInReEx);
    }

    [Fact]
    public void SameIdButDifferentName_IsRefused_AndListsTheOperatorDetail()
    {
        var site = Site(1, "Different");
        site.ContactName = "Someone";
        site.Code1 = "A1181";

        IsNewSiteClassifier
            .Classify(site, ReExHolds((1, "Registered")))
            .RefusedBecause.Should()
            .Equal(IsNewSiteClassifier.NotInReEx, "contactName", "code1");
    }

    [Fact]
    public void ReExUnreadable_IsRefused() =>
        IsNewSiteClassifier
            .Classify(Site(1, "A"), ReExSiteLookup.Failed("status 503"))
            .RefusedBecause.Should()
            .Equal("reExUnavailable (status 503)");

    [Fact]
    public void UnprovenSiteWithoutAReExLookup_Throws()
    {
        var act = () => IsNewSiteClassifier.Classify(Site(1, "A"), null);

        act.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(IsNewSiteVerdict.ProvablyCorrupt, "PROVABLY-CORRUPT")]
    [InlineData(IsNewSiteVerdict.PromotedCorrectable, "PROMOTED-CORRECTABLE")]
    [InlineData(IsNewSiteVerdict.AmbiguousRefused, "AMBIGUOUS-REFUSED")]
    [InlineData(IsNewSiteVerdict.AlreadyCorrect, "ALREADY-CORRECT")]
    public void BucketNames_MatchManagementBe(IsNewSiteVerdict verdict, string name) =>
        IsNewSiteClassifier.BucketName(verdict).Should().Be(name);
}

// epr-2uxy: the audit and the correction against the real ephemeral replica set.
public sealed class IsNewSiteCorrectionTests : IDisposable
{
    private const string ReExOrganisationId = "6512a0f0c0ffee0000000001";
    private const string ReExRegistrationId = "6512a0f0c0ffee0000000002";
    private const string DeployedAt = "2026-08-11T09:00:00Z";

    private static readonly DateTime BeforeDeploy = new(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterDeploy = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _applications;
    private readonly IReExApiAdapter _reEx = Substitute.For<IReExApiAdapter>();
    private readonly CapturingLogger<IsNewSiteCorrectionMigration> _correctionLog = new();
    private readonly CapturingLogger<IsNewSiteAudit> _auditLog = new();
    private readonly ServiceProvider _metrics = new ServiceCollection()
        .AddMetrics()
        .BuildServiceProvider();

    public IsNewSiteCorrectionTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("epr2uxy");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _applications = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );
        // ReEx holds sites 1 and 2 on the registration.
        _reEx
            .GetAccreditationAsync(
                ReExOrganisationId,
                ReExRegistrationId,
                MaterialType.Plastic,
                2026
            )
            .Returns(
                ReExResult<ReExAccreditationDto>.Success(
                    new ReExAccreditationDto
                    {
                        OverseasSites =
                        [
                            IsNewSiteClassifierTests.Site(1, "Carried Over Ltd", false, "001"),
                            IsNewSiteClassifierTests.Site(2, "Promoted SA", false, "002"),
                        ],
                    },
                    200
                )
            );
    }

    public void Dispose()
    {
        _metrics.Dispose();
        _factory.GetClient().DropDatabase(_databaseName);
    }

    // Site 1: ReEx-sourced, wrongly true. Site 2: ReEx-sourced, promoted, wrongly true.
    // Site 7: operator-added (OrsId). Site 99: no OrsId and not in ReEx (could be the PATCH hole).
    // Site 3: correctly false.
    private static List<OverseasSiteModel> MixedSites() =>
        [
            IsNewSiteClassifierTests.Site(1, "Carried Over Ltd"),
            IsNewSiteClassifierTests.PromotedWithFullDetail(2, "Promoted SA"),
            IsNewSiteClassifierTests.Site(7, "Operator Added GmbH", orsId: "007"),
            IsNewSiteClassifierTests.Site(99, "Unproven"),
            IsNewSiteClassifierTests.Site(3, "Not New", isNewSite: false),
        ];

    private async Task<AccreditationApplicationModel> SeedAsync(
        DateTime createdAt,
        List<OverseasSiteModel>? sites = null,
        ApplicationStatus status = ApplicationStatus.Queried
    ) =>
        (
            await _applications.CreateAsync(
                new AccreditationApplicationModel
                {
                    Id = ObjectId.GenerateNewId(),
                    OrganisationId = ReExOrganisationId,
                    OrganisationName = "Acme Recycling Ltd",
                    RegistrationId = ReExRegistrationId,
                    IsExporter = true,
                    Nation = Nation.England,
                    MaterialType = MaterialType.Plastic,
                    Year = 2027,
                    ApplicationStatus = status,
                    ApplicationReference = $"APP-{Guid.NewGuid():N}",
                    CaseManagementWorkItemId = Guid.NewGuid(),
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt,
                    DateLastEdited = createdAt,
                    OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites = sites ?? MixedSites(),
                    },
                }
            )
        )!;

    private async Task<AccreditationApplicationModel> StoredAsync(
        AccreditationApplicationModel application
    ) => (await _applications.GetByIdAsync(ReExOrganisationId, application.Id!.Value.ToString()))!;

    private static Dictionary<int, bool> Flags(AccreditationApplicationModel application) =>
        application.OverseasSites!.Sites.ToDictionary(s => s.SiteId, s => s.IsNewSite);

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static IConfiguration Apply(string apply = "true") =>
        Config(
            (IsNewSiteConfig.CorrectKey, "true"),
            (IsNewSiteConfig.SpotCheckConfirmedByKey, "tom.halley"),
            (IsNewSiteConfig.ApplyKey, apply),
            (IsNewSiteConfig.DeployedAtKey, DeployedAt)
        );

    private ServiceProvider Services(IDualWriteUnitOfWork? unitOfWork = null, ApplicationGroupStores? stores = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IAccreditationApplicationPersistence>(_applications)
            .AddSingleton(_reEx)
            .AddSingleton(unitOfWork ?? new LegacyOnlyUnitOfWork());
        if (stores is not null)
            services.AddSingleton(stores);
        return services.BuildServiceProvider();
    }

    private IsNewSiteCorrectionMigration Correction(IConfiguration configuration, ServiceProvider? services = null) =>
        new(services ?? Services(), configuration, _correctionLog);

    private IsNewSiteAudit Audit(IConfiguration configuration) =>
        new(Services(), configuration, _auditLog);

    // --- AC01: the audit ---------------------------------------------------------------------

    [Fact]
    public async Task Audit_CountsEveryBucketWithApplicationIds_AndWritesNothing()
    {
        var inWindow = await SeedAsync(BeforeDeploy);
        var afterFix = await SeedAsync(AfterDeploy);
        var versionBefore = (await StoredAsync(inWindow)).Version;

        var result = await Audit(
                Config((IsNewSiteConfig.AuditKey, "true"), (IsNewSiteConfig.DeployedAtKey, DeployedAt))
            )
            .RunAsync(Ct);

        result!.ApplicationsScanned.Should().Be(1);
        result
            .SiteCounts.Should()
            .BeEquivalentTo(
                new Dictionary<IsNewSiteVerdict, int>
                {
                    [IsNewSiteVerdict.ProvablyCorrupt] = 1,
                    [IsNewSiteVerdict.PromotedCorrectable] = 1,
                    [IsNewSiteVerdict.AlreadyCorrect] = 1,
                    [IsNewSiteVerdict.AmbiguousRefused] = 1,
                    [IsNewSiteVerdict.NotFlaggedNew] = 1,
                }
            );
        result
            .ApplicationIdsByBucket[IsNewSiteVerdict.ProvablyCorrupt]
            .Should()
            .Equal(inWindow.Id.ToString());
        result.ApplicationIdsByBucket.Values.SelectMany(ids => ids).Should().NotContain(afterFix.Id.ToString());

        var stored = await StoredAsync(inWindow);
        stored.Version.Should().Be(versionBefore);
        Flags(stored).Should().BeEquivalentTo(Flags(inWindow));
        _auditLog
            .Entries.Should()
            .Contain(e => e.Message.Contains("PROVABLY-CORRUPT: 1 sites on 1 applications"))
            .And.Contain(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("notInReEx"));
    }

    [Fact]
    public async Task Audit_Disabled_ReadsNothing()
    {
        await SeedAsync(BeforeDeploy);

        (await Audit(Config((IsNewSiteConfig.DeployedAtKey, DeployedAt))).RunAsync(Ct))
            .Should()
            .BeNull();

        await _reEx.DidNotReceiveWithAnyArgs().GetAccreditationAsync(default!, default!, default, default);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a date")]
    public async Task Audit_EnabledWithoutAUsableDeployedAt_RefusesAndLogsError(string? deployedAt)
    {
        await SeedAsync(BeforeDeploy);

        (await Audit(Config((IsNewSiteConfig.AuditKey, "true"), (IsNewSiteConfig.DeployedAtKey, deployedAt))).RunAsync(Ct))
            .Should()
            .BeNull();

        _auditLog.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Which.Message.Should().Contain(IsNewSiteConfig.DeployedAtKey);
        await _reEx.DidNotReceiveWithAnyArgs().GetAccreditationAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Audit_MalformedFlag_RefusesAndLogsError()
    {
        (await Audit(Config((IsNewSiteConfig.AuditKey, "yes"), (IsNewSiteConfig.DeployedAtKey, DeployedAt))).RunAsync(Ct))
            .Should()
            .BeNull();

        _auditLog.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Which.Message.Should().Contain(IsNewSiteConfig.AuditKey);
    }

    // --- AC02: the gates -----------------------------------------------------------------------

    public static TheoryData<string, (string, string?)[]> MissingGates =>
        new()
        {
            { "correction off", [(IsNewSiteConfig.SpotCheckConfirmedByKey, "tom"), (IsNewSiteConfig.ApplyKey, "true"), (IsNewSiteConfig.DeployedAtKey, DeployedAt)] },
            { "no spot check", [(IsNewSiteConfig.CorrectKey, "true"), (IsNewSiteConfig.ApplyKey, "true"), (IsNewSiteConfig.DeployedAtKey, DeployedAt)] },
            { "blank spot check", [(IsNewSiteConfig.CorrectKey, "true"), (IsNewSiteConfig.SpotCheckConfirmedByKey, " "), (IsNewSiteConfig.ApplyKey, "true"), (IsNewSiteConfig.DeployedAtKey, DeployedAt)] },
            { "no deployed-at", [(IsNewSiteConfig.CorrectKey, "true"), (IsNewSiteConfig.SpotCheckConfirmedByKey, "tom"), (IsNewSiteConfig.ApplyKey, "true")] },
            { "malformed deployed-at", [(IsNewSiteConfig.CorrectKey, "true"), (IsNewSiteConfig.SpotCheckConfirmedByKey, "tom"), (IsNewSiteConfig.ApplyKey, "true"), (IsNewSiteConfig.DeployedAtKey, "soon")] },
            { "malformed apply", [(IsNewSiteConfig.CorrectKey, "true"), (IsNewSiteConfig.SpotCheckConfirmedByKey, "tom"), (IsNewSiteConfig.ApplyKey, "1"), (IsNewSiteConfig.DeployedAtKey, DeployedAt)] },
            { "malformed correct", [(IsNewSiteConfig.CorrectKey, "on"), (IsNewSiteConfig.SpotCheckConfirmedByKey, "tom"), (IsNewSiteConfig.ApplyKey, "true"), (IsNewSiteConfig.DeployedAtKey, DeployedAt)] },
        };

    [Theory]
    [MemberData(nameof(MissingGates))]
    public async Task Correction_AnyGateMissingOrMalformed_WritesNothing(
        string gate,
        (string, string?)[] config
    )
    {
        var application = await SeedAsync(BeforeDeploy);
        var versionBefore = (await StoredAsync(application)).Version;

        var result = await Correction(Config(config)).RunAsync(Ct);

        var stored = await StoredAsync(application);
        stored.Version.Should().Be(versionBefore, gate);
        Flags(stored).Should().BeEquivalentTo(Flags(application), gate);
        if (gate == "correction off")
        {
            result.Should().Be(RunResult.Disabled);
            _correctionLog.Entries.Should().BeEmpty();
        }
        else
        {
            result.Should().Be(RunResult.Refused, gate);
            _correctionLog.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error, gate);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    public async Task Correction_DryRun_ReportsButWritesNothing(string? apply)
    {
        var application = await SeedAsync(BeforeDeploy);
        var versionBefore = (await StoredAsync(application)).Version;
        var config = apply is null
            ? Config(
                (IsNewSiteConfig.CorrectKey, "true"),
                (IsNewSiteConfig.SpotCheckConfirmedByKey, "tom.halley"),
                (IsNewSiteConfig.DeployedAtKey, DeployedAt)
            )
            : Apply(apply);

        (await Correction(config).RunAsync(Ct)).Should().Be(RunResult.DryRun);

        var stored = await StoredAsync(application);
        stored.Version.Should().Be(versionBefore);
        Flags(stored).Should().BeEquivalentTo(Flags(application));
        _correctionLog
            .Entries.Should()
            .Contain(e => e.Message.Contains("DRY RUN would correct") && e.Message.Contains("1 'Carried Over Ltd'"));
    }

    [Fact]
    public async Task Correction_Apply_CorrectsOnlyTheCorrectableBuckets_AndIsIdempotent()
    {
        var application = await SeedAsync(BeforeDeploy);
        var afterFix = await SeedAsync(AfterDeploy);

        (await Correction(Apply()).RunAsync(Ct)).Should().Be(RunResult.Applied);

        var stored = await StoredAsync(application);
        Flags(stored)
            .Should()
            .BeEquivalentTo(
                new Dictionary<int, bool>
                {
                    [1] = false,
                    [2] = false,
                    [7] = true,
                    [99] = true,
                    [3] = false,
                }
            );
        // Nothing else on the sites moved: the promoted site keeps its promotion and detail.
        stored.OverseasSites!.Sites.Single(s => s.SiteId == 2).Should().BeEquivalentTo(
            application.OverseasSites!.Sites.Single(s => s.SiteId == 2),
            o => o.Excluding(s => s.IsNewSite)
        );
        stored.Version.Should().Be(application.Version + 1);
        Flags(await StoredAsync(afterFix)).Should().BeEquivalentTo(Flags(afterFix));
        _correctionLog
            .Entries.Should()
            .Contain(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("REFUSED") && e.Message.Contains("99"));

        // Second run: nothing left to correct, nothing written.
        _correctionLog.Entries.Clear();
        (await Correction(Apply()).RunAsync(Ct)).Should().Be(RunResult.Applied);
        (await StoredAsync(application)).Version.Should().Be(stored.Version);
        _correctionLog.Entries.Should().NotContain(e => e.Message.Contains("APPLIED"));
    }

    [Fact]
    public async Task Correction_ReExUnavailable_RefusesEveryUnprovenSite()
    {
        _reEx
            .GetAccreditationAsync(default!, default!, default, default)
            .ReturnsForAnyArgs(ReExResult<ReExAccreditationDto>.Fail(new ReExError(ReExErrorKind.ServerError, "down"), 503));
        var application = await SeedAsync(BeforeDeploy);

        await Correction(Apply()).RunAsync(Ct);

        Flags(await StoredAsync(application)).Should().BeEquivalentTo(Flags(application));
        _correctionLog
            .Entries.Should()
            .Contain(e => e.LogLevel == LogLevel.Error && e.Message.Contains("503"));
    }

    // --- AC04: a query-resubmit after the correction ---------------------------------------------

    [Theory]
    [InlineData("overseas-reprocessing-sites", "OverseasSites")]
    [InlineData("broadly-equivalent-standards", "BesEvidence")]
    public async Task Resubmit_AfterCorrection_SendsIsNewSiteFalseForTheCorrectedSites(
        string sectionKey,
        string section
    )
    {
        var application = await SeedAsync(BeforeDeploy);
        await Correction(Apply()).RunAsync(Ct);

        var sections = await HttpCaseWorkingApiAdapterTests.CapturedResumeSections(
            await StoredAsync(application),
            sectionKey
        );

        var sent = sections
            .GetProperty(section)
            .GetProperty("sites")
            .EnumerateArray()
            .ToDictionary(s => s.GetProperty("siteName").GetString()!, s => s.GetProperty("isNewSite").GetBoolean());
        sent.Should()
            .BeEquivalentTo(
                new Dictionary<string, bool>
                {
                    ["Carried Over Ltd"] = false,
                    ["Promoted SA"] = false,
                    ["Operator Added GmbH"] = true,
                    ["Unproven"] = true,
                    ["Not New"] = false,
                }
            );
    }

    // --- AC03: the dual-write domain model ---------------------------------------------------

    [Fact]
    public async Task Correction_WithDualWrite_CorrectsTheApplicationVersionAndTheOwnOrs()
    {
        var clock = new TestClock(new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        var stores = new ApplicationGroupStores(
            new OrganisationPersistence(_factory, logs),
            new RegistrationPersistence(_factory, logs, clock),
            new OverseasSitePersistence(_factory, logs, clock),
            new ApplicationVersionPersistence(_factory, logs, clock),
            new AccreditationPersistence(_factory, logs, clock),
            new MigrationConflictPersistence(_factory, logs)
        );
        var migrator = new ApplicationGroupMigrator(_applications, stores, clock);
        var unitOfWork = new DualWriteUnitOfWork(
            _factory,
            migrator,
            new ApplicationModelSync(_applications, stores, clock, NullLogger<ApplicationModelSync>.Instance),
            new DualWriteMetrics(_metrics.GetRequiredService<IMeterFactory>()),
            NullLogger<DualWriteUnitOfWork>.Instance
        );
        var application = await SeedAsync(
            BeforeDeploy,
            [IsNewSiteClassifierTests.Site(1, "Carried Over Ltd")]
        );
        await InsertRaceRetry.RunInTransactionAsync(
            _factory,
            (session, token) => migrator.EnsureMigratedAsync(application.Id!.Value, session, token),
            _ => { },
            Ct
        );
        (await OrsForSiteAsync(stores, application, 1)).Origin.Should().Be(SiteOrigin.Operator, "precondition: the wrong true made it a proposal");

        (await Correction(Apply(), Services(unitOfWork, stores)).RunAsync(Ct))
            .Should()
            .Be(RunResult.Applied);

        Flags(await StoredAsync(application))[1].Should().BeFalse();
        var entry = (await CurrentApplicationVersionAsync(stores, application))
            .AsAccreditation()
            .ApplicationOverseasSites.Single(e => e.SiteId == 1);
        entry.IsNewSite.Should().BeFalse();
        var ors = await OrsForSiteAsync(stores, application, 1);
        ors.Origin.Should().Be(SiteOrigin.ReEx);
        ors.Status.Should().Be(OverseasSiteStatus.Active);
        ors.ProposedByApplicationId.Should().BeNull();
    }

    private static async Task<ApplicationEntity> CurrentApplicationVersionAsync(
        ApplicationGroupStores stores,
        AccreditationApplicationModel application
    ) =>
        (
            await stores.Applications.Versions.FindCurrentAsync(
                Builders<ApplicationEntity>.Filter.Eq(a => a.EntityId, application.Id!.Value),
                null,
                Ct
            )
        )!;

    private static async Task<OverseasSiteEntity> OrsForSiteAsync(
        ApplicationGroupStores stores,
        AccreditationApplicationModel application,
        int siteId
    )
    {
        var entry = (await CurrentApplicationVersionAsync(stores, application))
            .AsAccreditation()
            .ApplicationOverseasSites.Single(e => e.SiteId == siteId);
        return (
            await stores.OverseasSites.Versions.FindCurrentAsync(
                Builders<OverseasSiteEntity>.Filter.Eq(o => o.EntityId, entry.OverseasSiteId!.Value),
                null,
                Ct
            )
        )!;
    }
}
