using System.Net;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RunMode = EprRegisterEnrolBackend.AccreditationApplication.Startup.ChargeCorrectionMigration.RunMode;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-766: runs against the real ephemeral mongod so the candidate query is exercised exactly as
// production runs it, with the Case Management service's adapter substituted. Log assertions are
// on the RENDERED message (the CapturingLogger formats it), because the message text is all CDP
// indexes, so a malformed template or a missing value would otherwise pass unseen.
public sealed class ChargeCorrectionMigrationTests : IDisposable
{
    // £3,276 + £328 x 2 sites = £3,932; £3,965 + £328 = £4,293.
    private const int Over10000OneSitePence = 429_300;
    private const int UpTo10000TwoSitesPence = 393_200;

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _persistence;
    private readonly ICaseWorkingApiAdapter _caseWorking = Substitute.For<ICaseWorkingApiAdapter>();
    private readonly Dictionary<Guid, CaseWorkingWorkItem> _workItems = [];
    private readonly CapturingLogger<ChargeCorrectionMigration> _logger = new();

    public ChargeCorrectionMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra766");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );

        _caseWorking
            .GetWorkItemAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _workItems[call.Arg<Guid>()]);
        _caseWorking
            .SetChargeAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SetChargeOutcome.Updated);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private async Task<(Guid WorkItemId, AccreditationApplicationModel Application)> SeedAsync(
        PlannedTonnageBand? band = PlannedTonnageBand.Over10000,
        int sites = 1,
        ApplicationStatus status = ApplicationStatus.Queried,
        bool hasWorkItem = true,
        int? storedPence = 218_400,
        string workItemState = "queried",
        bool dulyMade = false
    )
    {
        var workItemId = Guid.NewGuid();
        var application = new AccreditationApplicationModel
        {
            OrganisationId = "org-1",
            OrgId = 500500,
            Year = 2027,
            MaterialType = MaterialType.Plastic,
            ApplicationStatus = status,
            ApplicationReference = $"APP27{Guid.NewGuid():N}"[..14],
            CaseManagementWorkItemId = hasWorkItem ? workItemId : null,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = Enumerable
                    .Range(1, sites)
                    .Select(i => new OverseasSiteModel { SiteId = i, SiteName = $"Site {i}" })
                    .ToList(),
            },
        };
        application.Prns.PlannedTonnageBand = band;
        await _persistence.CreateAsync(application);
        _workItems[workItemId] = new CaseWorkingWorkItem(workItemState, storedPence, dulyMade);
        return (workItemId, application);
    }

    private ChargeCorrectionMigration CreateMigration(
        bool enabled = true,
        bool apply = false,
        bool registerServices = true
    )
    {
        var services = new ServiceCollection();
        if (registerServices)
        {
            services.AddSingleton<IAccreditationApplicationPersistence>(_persistence);
            services.AddSingleton(_caseWorking);
        }

        return new ChargeCorrectionMigration(
            services.BuildServiceProvider(),
            Options.Create(new ChargeCorrectionOptions { Enabled = enabled, Apply = apply }),
            _logger
        );
    }

    private Task<ChargeCorrectionMigration.Summary> RunAsync(bool apply) =>
        CreateMigration(apply: apply).RunAsync(TestContext.Current.CancellationToken);

    private List<string> Logged(LogLevel level) =>
        _logger.Entries.Where(e => e.LogLevel == level).Select(e => e.Message).ToList();

    private List<string> OutcomeLines() =>
        _logger
            .Entries.Select(e => e.Message)
            .Where(m => m.StartsWith("RA-766 charge correction: outcome=", StringComparison.Ordinal))
            .ToList();

    private async Task DidNotWriteAsync() =>
        await _caseWorking
            .DidNotReceive()
            .SetChargeAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

    [Fact]
    public async Task Disabled_does_nothing_and_resolves_nothing()
    {
        await SeedAsync();

        // Nothing registered: resolving the persistence or the adapter would throw.
        var summary = await CreateMigration(enabled: false, apply: true, registerServices: false)
            .RunAsync(TestContext.Current.CancellationToken);

        summary.Mode.Should().Be(RunMode.Disabled);
        _logger.Entries.Should().BeEmpty();
        await _caseWorking.DidNotReceiveWithAnyArgs().GetWorkItemAsync(default, default);
    }

    [Fact]
    public async Task Dry_run_logs_would_update_with_every_value_and_writes_nothing()
    {
        var (workItemId, application) = await SeedAsync(storedPence: 218_400);

        var summary = await RunAsync(apply: false);

        summary.Mode.Should().Be(RunMode.DryRun);
        summary.WouldUpdate.Should().Be(1);
        summary.Updated.Should().Be(0);
        await DidNotWriteAsync();
        OutcomeLines()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "RA-766 charge correction: outcome=would-update "
                    + $"applicationReference={application.ApplicationReference} workItemId={workItemId} "
                    + $"oldPence=218400 newPence={Over10000OneSitePence} dulyMade=false"
            );
    }

    [Fact]
    public async Task Logs_a_start_line_with_the_mode_and_a_summary_line_with_counts()
    {
        await SeedAsync();

        await RunAsync(apply: false);

        Logged(LogLevel.Information)
            .Should()
            .Contain("RA-766 charge correction starting: mode=dry-run candidates=1");
        Logged(LogLevel.Information)
            .Should()
            .Contain(
                "RA-766 charge correction summary: mode=dry-run candidates=1 updated=0 would-update=1 "
                    + "unchanged=0 skipped-no-band=0 skipped-terminal=0 failed=0"
            );
    }

    [Fact]
    public async Task Apply_sets_the_calculated_charge_on_a_stale_work_item()
    {
        var (workItemId, application) = await SeedAsync(storedPence: 218_400);

        var summary = await RunAsync(apply: true);

        summary.Mode.Should().Be(RunMode.Apply);
        summary.Updated.Should().Be(1);
        await _caseWorking
            .Received(1)
            .SetChargeAsync(workItemId, Over10000OneSitePence, Arg.Any<CancellationToken>());
        OutcomeLines()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "RA-766 charge correction: outcome=updated "
                    + $"applicationReference={application.ApplicationReference} workItemId={workItemId} "
                    + $"oldPence=218400 newPence={Over10000OneSitePence} dulyMade=false"
            );
        Logged(LogLevel.Information).Should().Contain(m => m.Contains("mode=apply"));
    }

    [Fact]
    public async Task Apply_sets_a_charge_on_a_work_item_that_has_none()
    {
        var (workItemId, _) = await SeedAsync(storedPence: null);

        await RunAsync(apply: true);

        await _caseWorking
            .Received(1)
            .SetChargeAsync(workItemId, Over10000OneSitePence, Arg.Any<CancellationToken>());
        OutcomeLines().Should().ContainSingle(m => m.Contains("oldPence=none"));
    }

    [Fact]
    public async Task A_duly_made_work_item_is_corrected_and_reported_as_duly_made()
    {
        var (workItemId, _) = await SeedAsync(workItemState: "duly-made", dulyMade: true);

        var summary = await RunAsync(apply: true);

        summary.Updated.Should().Be(1);
        await _caseWorking
            .Received(1)
            .SetChargeAsync(workItemId, Over10000OneSitePence, Arg.Any<CancellationToken>());
        OutcomeLines()
            .Should()
            .ContainSingle(m => m.Contains("outcome=updated") && m.Contains("dulyMade=true"));
    }

    [Fact]
    public async Task A_work_item_that_already_holds_the_charge_is_unchanged()
    {
        await SeedAsync(storedPence: Over10000OneSitePence);

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        await DidNotWriteAsync();
        OutcomeLines().Should().ContainSingle(m => m.Contains("outcome=unchanged"));
    }

    [Fact]
    public async Task An_application_with_no_tonnage_band_is_skipped_without_reading_the_work_item()
    {
        await SeedAsync(band: null);

        var summary = await RunAsync(apply: true);

        summary.SkippedNoBand.Should().Be(1);
        await _caseWorking.DidNotReceiveWithAnyArgs().GetWorkItemAsync(default, default);
        await DidNotWriteAsync();
        OutcomeLines()
            .Should()
            .ContainSingle(m =>
                m.Contains("outcome=skipped-no-band")
                && m.Contains("oldPence=none")
                && m.Contains("newPence=none")
                && m.Contains("dulyMade=unknown")
            );
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("approved-pending-contact")]
    [InlineData("rejected-pending-contact")]
    public async Task A_work_item_closed_in_case_management_is_skipped(string workItemState)
    {
        // The application can still look open here: the pending-contact states show as
        // AwaitingDecision.
        await SeedAsync(status: ApplicationStatus.AwaitingDecision, workItemState: workItemState);

        var summary = await RunAsync(apply: true);

        summary.SkippedTerminal.Should().Be(1);
        await DidNotWriteAsync();
        OutcomeLines().Should().ContainSingle(m => m.Contains("outcome=skipped-terminal"));
    }

    [Fact]
    public async Task A_work_item_that_closes_between_the_read_and_the_write_is_skipped()
    {
        await SeedAsync();
        _caseWorking
            .SetChargeAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SetChargeOutcome.Closed);

        var summary = await RunAsync(apply: true);

        summary.SkippedTerminal.Should().Be(1);
        summary.Updated.Should().Be(0);
    }

    [Fact]
    public async Task A_charge_the_write_finds_already_correct_is_unchanged()
    {
        await SeedAsync();
        _caseWorking
            .SetChargeAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SetChargeOutcome.Unchanged);

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        summary.Updated.Should().Be(0);
    }

    [Fact]
    public async Task One_failure_is_counted_with_its_status_only_and_the_run_carries_on()
    {
        var (failingId, failing) = await SeedAsync();
        var (okId, _) = await SeedAsync(
            band: PlannedTonnageBand.UpTo10000,
            sites: 2,
            storedPence: 100
        );
        // The exception message carries something that must never reach the logs.
        _caseWorking
            .GetWorkItemAsync(failingId, Arg.Any<CancellationToken>())
            .ThrowsAsync(
                new HttpRequestException(
                    "body said jane.doe@example.com",
                    inner: null,
                    HttpStatusCode.InternalServerError
                )
            );

        var summary = await RunAsync(apply: true);

        summary.Failed.Should().Be(1);
        summary.Updated.Should().Be(1);
        await _caseWorking
            .Received(1)
            .SetChargeAsync(okId, UpTo10000TwoSitesPence, Arg.Any<CancellationToken>());
        Logged(LogLevel.Error)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                "RA-766 charge correction: outcome=failed "
                    + $"applicationReference={failing.ApplicationReference} workItemId={failingId} "
                    + "error=HttpRequestException status=500"
            );
        _logger.Entries.Should().NotContain(e => e.Message.Contains("jane.doe"));
        _logger.Entries.Should().OnlyContain(e => e.Exception == null);
    }

    [Fact]
    public async Task A_failure_with_no_http_status_logs_the_exception_type_only()
    {
        var (workItemId, _) = await SeedAsync();
        _caseWorking
            .GetWorkItemAsync(workItemId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("CaseWorking API URL is not configured."));

        await RunAsync(apply: true);

        Logged(LogLevel.Error)
            .Should()
            .ContainSingle(m => m.EndsWith("error=InvalidOperationException status=none"));
    }

    [Theory]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task Closed_applications_are_not_candidates(ApplicationStatus status)
    {
        await SeedAsync(status: status);

        var summary = await RunAsync(apply: true);

        summary.Candidates.Should().Be(0);
        await _caseWorking.DidNotReceiveWithAnyArgs().GetWorkItemAsync(default, default);
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Started)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.DulyMade)]
    [InlineData(ApplicationStatus.Queried)]
    [InlineData(ApplicationStatus.Updated)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    public async Task Open_applications_with_a_work_item_are_candidates(ApplicationStatus status)
    {
        await SeedAsync(status: status);

        var summary = await RunAsync(apply: false);

        summary.Candidates.Should().Be(1);
    }

    [Fact]
    public async Task An_application_with_no_work_item_is_not_a_candidate()
    {
        await SeedAsync(hasWorkItem: false);

        var summary = await RunAsync(apply: true);

        summary.Candidates.Should().Be(0);
    }

    [Fact]
    public async Task Running_again_after_apply_changes_nothing()
    {
        var (workItemId, _) = await SeedAsync(storedPence: 218_400);
        await RunAsync(apply: true);
        // The Case Management service now holds the corrected charge.
        _workItems[workItemId] = _workItems[workItemId] with { ChargeAmountPence = Over10000OneSitePence };
        _caseWorking.ClearReceivedCalls();

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        summary.Updated.Should().Be(0);
        await DidNotWriteAsync();
    }

    [Fact]
    public async Task A_dry_run_leaves_every_application_untouched()
    {
        await SeedAsync();
        var before = await ReadAllAsync();

        await RunAsync(apply: false);

        (await ReadAllAsync()).Should().BeEquivalentTo(before);
    }

    private async Task<List<AccreditationApplicationModel>> ReadAllAsync() =>
        await _factory
            .GetCollection<AccreditationApplicationModel>("accreditationApplications")
            .Find(FilterDefinition<AccreditationApplicationModel>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public void Options_default_to_off_and_bind_from_the_migrations_section()
    {
        new ChargeCorrectionOptions().Enabled.Should().BeFalse();
        new ChargeCorrectionOptions().Apply.Should().BeFalse();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Migrations:Ra766ChargeCorrection:Enabled"] = "true",
                    ["Migrations:Ra766ChargeCorrection:Apply"] = "true",
                }
            )
            .Build();

        var options = configuration
            .GetSection(ChargeCorrectionOptions.SectionName)
            .Get<ChargeCorrectionOptions>();

        options.Should().NotBeNull();
        options!.Enabled.Should().BeTrue();
        options.Apply.Should().BeTrue();
    }

    [Fact]
    public void The_shipped_appsettings_has_the_migration_off_and_logs_it_at_information()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

        configuration
            .GetSection(ChargeCorrectionOptions.SectionName)
            .Get<ChargeCorrectionOptions>()
            .Should()
            .BeEquivalentTo(new ChargeCorrectionOptions { Enabled = false, Apply = false });
        // CDP defaults to Warning, so without this nothing appears in ext-test or prod.
        configuration[
            "Serilog:MinimumLevel:Override:EprRegisterEnrolBackend.AccreditationApplication.Startup.ChargeCorrectionMigration"
        ]
            .Should()
            .Be("Information");
    }
}
