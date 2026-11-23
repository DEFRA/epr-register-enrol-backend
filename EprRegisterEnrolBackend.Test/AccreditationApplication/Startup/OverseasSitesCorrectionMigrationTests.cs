using System.Net;
using System.Text.Json;
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
using RunMode = EprRegisterEnrolBackend.AccreditationApplication.Startup.OverseasSitesCorrectionMigration.RunMode;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-768: runs against the real ephemeral mongod so the candidate query is exercised exactly as
// production runs it, with the Case Management service's adapter substituted. Log assertions are
// on the RENDERED message (the CapturingLogger formats it), because the message text is all CDP
// indexes, so a malformed template or a missing value would otherwise pass unseen.
public sealed class OverseasSitesCorrectionMigrationTests : IDisposable
{
    private const string Reviewer = "A Reviewer";

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _persistence;
    private readonly ICaseWorkingApiAdapter _caseWorking = Substitute.For<ICaseWorkingApiAdapter>();
    private readonly Dictionary<Guid, CaseWorkingWorkItem> _workItems = [];
    private readonly CapturingLogger<OverseasSitesCorrectionMigration> _logger = new();

    public OverseasSitesCorrectionMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra768");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );

        _caseWorking
            .GetWorkItemAsync(
                Arg.Any<Guid>(),
                CaseWorkingCorrectionActor.OverseasSitesCorrection,
                Arg.Any<CancellationToken>()
            )
            .Returns(call => _workItems[call.Arg<Guid>()]);
        // By default the Case Management service removes every site it is sent.
        _caseWorking
            .RemoveOverseasSitesAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(call => new RemoveOverseasSitesResult(
                RemoveOverseasSitesOutcome.Updated,
                call.Arg<IReadOnlyList<CaseWorkingSiteIds>>()
            ));
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static OverseasSiteModel Site(int siteId, string? orsId, bool selected) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = $"Site {siteId}",
            Selected = selected,
        };

    private static CaseWorkingSiteIds Held(string? orsId, string? siteId) => new(orsId, siteId);

    // Default: the operator kept ORS-1 and deselected ORS-2; the work item still holds both.
    private async Task<(Guid WorkItemId, AccreditationApplicationModel Application)> SeedAsync(
        IReadOnlyList<OverseasSiteModel>? ours = null,
        IReadOnlyList<CaseWorkingSiteIds>? held = null,
        ApplicationStatus status = ApplicationStatus.Updated,
        bool hasWorkItem = true,
        string? workItemState = "in-assessment",
        bool dulyMade = false,
        bool noOverseasSites = false
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
            OverseasSites = noOverseasSites
                ? null
                : new AccreditationApplicationOverseasSites
                {
                    Sites = (ours ?? [Site(1, "ORS-1", true), Site(2, "ORS-2", false)]).ToList(),
                },
        };
        await _persistence.CreateAsync(application);
        _workItems[workItemId] = new CaseWorkingWorkItem(
            workItemState,
            null,
            dulyMade,
            held ?? [Held("ORS-1", "1"), Held("ORS-2", "2")]
        );
        return (workItemId, application);
    }

    private OverseasSitesCorrectionMigration CreateMigration(
        bool enabled = true,
        bool apply = false,
        string? spotCheckConfirmedBy = Reviewer,
        bool registerServices = true
    )
    {
        var services = new ServiceCollection();
        if (registerServices)
        {
            services.AddSingleton<IAccreditationApplicationPersistence>(_persistence);
            services.AddSingleton(_caseWorking);
        }

        return new OverseasSitesCorrectionMigration(
            services.BuildServiceProvider(),
            Options.Create(
                new OverseasSitesCorrectionOptions
                {
                    Enabled = enabled,
                    Apply = apply,
                    SpotCheckConfirmedBy = spotCheckConfirmedBy,
                }
            ),
            _logger
        );
    }

    private Task<OverseasSitesCorrectionMigration.Summary> RunAsync(bool apply) =>
        CreateMigration(apply: apply).RunAsync(TestContext.Current.CancellationToken);

    private List<string> Logged(LogLevel level) =>
        _logger.Entries.Where(e => e.LogLevel == level).Select(e => e.Message).ToList();

    private List<string> OutcomeLines() =>
        _logger
            .Entries.Select(e => e.Message)
            .Where(m =>
                m.StartsWith("RA-768 overseas sites correction: outcome=", StringComparison.Ordinal)
            )
            .ToList();

    private async Task DidNotWriteAsync() =>
        await _caseWorking
            .DidNotReceive()
            .RemoveOverseasSitesAsync(
                Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );

    private async Task DidNotReadAsync() =>
        await _caseWorking
            .DidNotReceive()
            .GetWorkItemAsync(
                Arg.Any<Guid>(),
                Arg.Any<CaseWorkingCorrectionActor>(),
                Arg.Any<CancellationToken>()
            );

    private static string Line(
        string outcome,
        AccreditationApplicationModel application,
        Guid workItemId,
        string sites
    ) =>
        $"RA-768 overseas sites correction: outcome={outcome} "
        + $"applicationReference={application.ApplicationReference} workItemId={workItemId} sites={sites}";

    private static string FailedLine(
        AccreditationApplicationModel application,
        Guid workItemId,
        string error,
        string status
    ) =>
        "RA-768 overseas sites correction: outcome=failed "
        + $"applicationReference={application.ApplicationReference} workItemId={workItemId} "
        + $"error={error} status={status}";

    [Fact]
    public async Task Disabled_does_nothing_and_resolves_nothing()
    {
        await SeedAsync();

        // Nothing registered: resolving the persistence or the adapter would throw.
        var summary = await CreateMigration(enabled: false, apply: true, registerServices: false)
            .RunAsync(TestContext.Current.CancellationToken);

        summary.Mode.Should().Be(RunMode.Disabled);
        _logger.Entries.Should().BeEmpty();
        await DidNotReadAsync();
        await DidNotWriteAsync();
    }

    [Fact]
    public async Task Dry_run_logs_the_sites_it_would_remove_and_calls_nothing_that_writes()
    {
        var (workItemId, application) = await SeedAsync();

        var summary = await RunAsync(apply: false);

        summary.Mode.Should().Be(RunMode.DryRun);
        summary.WouldUpdate.Should().Be(1);
        summary.Updated.Should().Be(0);
        await DidNotWriteAsync();
        OutcomeLines()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(Line("would-update", application, workItemId, "ORS-2/2"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Apply_without_a_spot_checker_runs_as_a_dry_run_and_logs_an_error(
        string? spotCheckConfirmedBy
    )
    {
        await SeedAsync();

        var summary = await CreateMigration(apply: true, spotCheckConfirmedBy: spotCheckConfirmedBy)
            .RunAsync(TestContext.Current.CancellationToken);

        summary.Mode.Should().Be(RunMode.DryRun);
        summary.WouldUpdate.Should().Be(1);
        await DidNotWriteAsync();
        Logged(LogLevel.Error)
            .Should()
            .ContainSingle()
            .Which.Should()
            .StartWith(
                "RA-768 overseas sites correction: Apply is set but SpotCheckConfirmedBy is not, so running as a DRY RUN."
            );
    }

    [Fact]
    public async Task Apply_removes_only_the_deselected_sites_and_logs_updated()
    {
        var (workItemId, application) = await SeedAsync(
            ours: [Site(1, "ORS-1", true), Site(2, "ORS-2", false), Site(3, "ORS-3", false)],
            held: [Held("ORS-1", "1"), Held("ORS-2", "2"), Held("ORS-3", "3")]
        );

        var summary = await RunAsync(apply: true);

        summary.Mode.Should().Be(RunMode.Apply);
        summary.Updated.Should().Be(1);
        await _caseWorking
            .Received(1)
            .RemoveOverseasSitesAsync(
                workItemId,
                Arg.Is<IReadOnlyList<CaseWorkingSiteIds>>(sites =>
                    sites.SequenceEqual(new[] { Held("ORS-2", "2"), Held("ORS-3", "3") })
                ),
                OverseasSitesCorrectionMigration.Reason,
                Arg.Any<CancellationToken>()
            );
        OutcomeLines()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(Line("updated", application, workItemId, "ORS-2/2,ORS-3/3"));
        // The spot-checker is a person's name: never logged.
        _logger.Entries.Should().NotContain(e => e.Message.Contains(Reviewer));
    }

    [Fact]
    public async Task A_work_item_site_without_an_orsId_is_matched_by_siteId()
    {
        var (workItemId, application) = await SeedAsync(
            ours: [Site(1, null, true), Site(2, null, false)],
            held: [Held(null, "1"), Held(null, "2")]
        );

        await RunAsync(apply: true);

        await _caseWorking
            .Received(1)
            .RemoveOverseasSitesAsync(
                workItemId,
                Arg.Is<IReadOnlyList<CaseWorkingSiteIds>>(sites =>
                    sites.SequenceEqual(new[] { Held(null, "2") })
                ),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
        OutcomeLines()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(Line("updated", application, workItemId, "none/2"));
    }

    [Fact]
    public async Task Sites_this_service_does_not_hold_are_logged_as_unmatched_and_never_sent()
    {
        // ORS-9 is unknown here; ORS-8 shares siteId 2 with a deselected site but has its own
        // orsId, so it is matched on orsId only and is unmatched too.
        var (workItemId, application) = await SeedAsync(
            held: [Held("ORS-1", "1"), Held("ORS-2", "2"), Held("ORS-9", "9"), Held("ORS-8", "2")]
        );

        var summary = await RunAsync(apply: true);

        summary.UnmatchedSites.Should().Be(2);
        summary.Updated.Should().Be(1);
        await _caseWorking
            .Received(1)
            .RemoveOverseasSitesAsync(
                workItemId,
                Arg.Is<IReadOnlyList<CaseWorkingSiteIds>>(sites =>
                    sites.SequenceEqual(new[] { Held("ORS-2", "2") })
                ),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
        Logged(LogLevel.Warning)
            .Should()
            .Equal(
                "RA-768 overseas sites correction: unmatched "
                    + $"applicationReference={application.ApplicationReference} workItemId={workItemId} site=ORS-9/9",
                "RA-768 overseas sites correction: unmatched "
                    + $"applicationReference={application.ApplicationReference} workItemId={workItemId} site=ORS-8/2"
            );
    }

    [Fact]
    public async Task An_application_with_no_overseas_sites_section_has_only_unmatched_sites()
    {
        var (workItemId, application) = await SeedAsync(noOverseasSites: true);

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        summary.UnmatchedSites.Should().Be(2);
        await DidNotWriteAsync();
        OutcomeLines().Should().Equal(Line("unchanged", application, workItemId, "none"));
    }

    [Fact]
    public async Task Nothing_to_remove_is_unchanged_without_a_write()
    {
        var (workItemId, application) = await SeedAsync(held: [Held("ORS-1", "1")]);

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        await DidNotWriteAsync();
        OutcomeLines().Should().Equal(Line("unchanged", application, workItemId, "none"));
    }

    [Fact]
    public async Task A_selected_site_is_kept_even_when_another_record_of_it_is_deselected()
    {
        await SeedAsync(
            ours: [Site(1, "ORS-1", true), Site(2, "ORS-1", false)],
            held: [Held("ORS-1", "1")]
        );

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        await DidNotWriteAsync();
    }

    [Fact]
    public async Task Every_site_already_gone_from_the_work_item_is_unchanged()
    {
        var (workItemId, application) = await SeedAsync();
        _caseWorking
            .RemoveOverseasSitesAsync(
                workItemId,
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new RemoveOverseasSitesResult(RemoveOverseasSitesOutcome.Unchanged, []));

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        summary.Updated.Should().Be(0);
        OutcomeLines().Should().Equal(Line("unchanged", application, workItemId, "none"));
    }

    [Fact]
    public async Task A_duly_made_work_item_is_listed_with_its_sites_and_not_changed()
    {
        var (workItemId, application) = await SeedAsync(dulyMade: true);

        var summary = await RunAsync(apply: true);

        summary.SkippedDulyMade.Should().Be(1);
        await DidNotWriteAsync();
        OutcomeLines()
            .Should()
            .Equal(Line("skipped-duly-made", application, workItemId, "ORS-2/2"));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("withdrawn")]
    [InlineData("approved-pending-contact")]
    [InlineData("rejected-pending-contact")]
    public async Task A_decided_work_item_is_listed_with_its_sites_and_not_changed(string state)
    {
        // Decided wins over duly made, as it does in the Case Management service.
        var (workItemId, application) = await SeedAsync(workItemState: state, dulyMade: true);

        var summary = await RunAsync(apply: true);

        summary.SkippedDecided.Should().Be(1);
        await DidNotWriteAsync();
        OutcomeLines()
            .Should()
            .Equal(Line("skipped-decided", application, workItemId, "ORS-2/2"));
    }

    [Theory]
    [InlineData(RemoveOverseasSitesOutcome.DulyMade, "skipped-duly-made")]
    [InlineData(RemoveOverseasSitesOutcome.Decided, "skipped-decided")]
    public async Task A_refusal_from_the_endpoint_is_the_matching_skipped_outcome(
        RemoveOverseasSitesOutcome refusal,
        string outcome
    )
    {
        // Duly made or decided between the read and the write.
        var (workItemId, application) = await SeedAsync();
        _caseWorking
            .RemoveOverseasSitesAsync(
                workItemId,
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new RemoveOverseasSitesResult(refusal, []));

        var summary = await RunAsync(apply: true);

        (summary.SkippedDulyMade + summary.SkippedDecided).Should().Be(1);
        summary.Updated.Should().Be(0);
        OutcomeLines().Should().Equal(Line(outcome, application, workItemId, "ORS-2/2"));
    }

    [Fact]
    public async Task A_queried_application_is_skipped_without_reading_the_work_item()
    {
        var (workItemId, application) = await SeedAsync(status: ApplicationStatus.Queried);

        var summary = await RunAsync(apply: true);

        summary.Candidates.Should().Be(1);
        summary.SkippedQueried.Should().Be(1);
        await DidNotReadAsync();
        await DidNotWriteAsync();
        OutcomeLines().Should().Equal(Line("skipped-queried", application, workItemId, "none"));
    }

    [Fact]
    public async Task A_read_failure_is_logged_with_its_status_only_and_the_run_carries_on()
    {
        var (failingId, failing) = await SeedAsync();
        var (okId, _) = await SeedAsync();
        // The exception message carries something that must never reach the logs.
        _caseWorking
            .GetWorkItemAsync(
                failingId,
                Arg.Any<CaseWorkingCorrectionActor>(),
                Arg.Any<CancellationToken>()
            )
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
            .RemoveOverseasSitesAsync(
                okId,
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
        Logged(LogLevel.Error)
            .Should()
            .Equal(FailedLine(failing, failingId, "HttpRequestException", "500"));
        _logger.Entries.Should().NotContain(e => e.Message.Contains("jane.doe"));
        _logger.Entries.Should().OnlyContain(e => e.Exception == null);
    }

    [Fact]
    public async Task A_write_failure_is_logged_and_the_run_carries_on()
    {
        var (failingId, failing) = await SeedAsync(
            held: [Held("ORS-1", "1"), Held("ORS-2", "2"), Held("ORS-9", "9")]
        );
        var (okId, _) = await SeedAsync();
        // A 409 concurrency-conflict, or any other non-refusal, reaches here as a throw.
        _caseWorking
            .RemoveOverseasSitesAsync(
                failingId,
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(new HttpRequestException("busy", inner: null, HttpStatusCode.Conflict));

        var summary = await RunAsync(apply: true);

        summary.Failed.Should().Be(1);
        summary.Updated.Should().Be(1);
        // The unmatched line was logged before the failure, so it is still counted.
        summary.UnmatchedSites.Should().Be(1);
        await _caseWorking
            .Received(1)
            .RemoveOverseasSitesAsync(
                okId,
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            );
        Logged(LogLevel.Error)
            .Should()
            .Equal(FailedLine(failing, failingId, "HttpRequestException", "409"));
    }

    [Fact]
    public async Task A_malformed_endpoint_response_is_a_failure_with_no_status()
    {
        var (workItemId, application) = await SeedAsync();
        _caseWorking
            .RemoveOverseasSitesAsync(
                workItemId,
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(new JsonException("no removed list"));

        var summary = await RunAsync(apply: true);

        summary.Failed.Should().Be(1);
        Logged(LogLevel.Error)
            .Should()
            .Equal(FailedLine(application, workItemId, "JsonException", "none"));
    }

    [Fact]
    public async Task An_unknown_endpoint_outcome_is_a_failure_not_handled()
    {
        var (workItemId, application) = await SeedAsync();
        _caseWorking
            .RemoveOverseasSitesAsync(
                workItemId,
                Arg.Any<IReadOnlyList<CaseWorkingSiteIds>>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new RemoveOverseasSitesResult((RemoveOverseasSitesOutcome)99, []));

        var summary = await RunAsync(apply: true);

        summary.Failed.Should().Be(1);
        Logged(LogLevel.Error)
            .Should()
            .Equal(FailedLine(application, workItemId, "InvalidOperationException", "none"));
    }

    [Fact]
    public async Task A_work_item_whose_overseas_sites_are_unreadable_is_a_failure_not_empty()
    {
        var (workItemId, application) = await SeedAsync();
        _workItems[workItemId] = _workItems[workItemId] with { OverseasSites = null };

        var summary = await RunAsync(apply: true);

        summary.Failed.Should().Be(1);
        summary.Unchanged.Should().Be(0);
        await DidNotWriteAsync();
        Logged(LogLevel.Error)
            .Should()
            .Equal(FailedLine(application, workItemId, "overseas-sites-unreadable", "none"));
    }

    [Fact]
    public async Task A_work_item_holding_a_site_with_neither_id_is_a_failure()
    {
        var (workItemId, application) = await SeedAsync(
            held: [Held("ORS-2", "2"), Held(null, null)]
        );

        var summary = await RunAsync(apply: true);

        summary.Failed.Should().Be(1);
        await DidNotWriteAsync();
        Logged(LogLevel.Error)
            .Should()
            .Equal(FailedLine(application, workItemId, "site-without-id", "none"));
    }

    [Fact]
    public async Task A_work_item_with_no_state_and_sites_to_remove_is_a_failure()
    {
        var (workItemId, application) = await SeedAsync(workItemState: null);

        var summary = await RunAsync(apply: true);

        summary.Failed.Should().Be(1);
        await DidNotWriteAsync();
        Logged(LogLevel.Error)
            .Should()
            .Equal(FailedLine(application, workItemId, "state-missing", "none"));
    }

    [Fact]
    public async Task Logs_a_start_line_with_the_mode_and_a_summary_line_with_counts()
    {
        await SeedAsync();
        await SeedAsync(status: ApplicationStatus.Queried);
        await SeedAsync(held: [Held("ORS-1", "1"), Held("ORS-9", "9")]);

        await RunAsync(apply: false);

        Logged(LogLevel.Information)
            .Should()
            .Contain("RA-768 overseas sites correction starting: mode=dry-run candidates=3");
        Logged(LogLevel.Information)
            .Should()
            .Contain(
                "RA-768 overseas sites correction summary: mode=dry-run candidates=3 updated=0 "
                    + "would-update=1 unchanged=1 skipped-queried=1 skipped-duly-made=0 "
                    + "skipped-decided=0 failed=0 unmatched-sites=1"
            );
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
        await DidNotReadAsync();
    }

    [Fact]
    public async Task An_application_with_no_work_item_is_not_a_candidate()
    {
        await SeedAsync(hasWorkItem: false);

        var summary = await RunAsync(apply: true);

        summary.Candidates.Should().Be(0);
    }

    [Fact]
    public async Task A_failure_reading_the_candidates_is_logged_and_does_not_stop_the_host()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence
            .GetOpenWithCaseManagementWorkItemAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("connection string with a secret"));
        var services = new ServiceCollection();
        services.AddSingleton(persistence);
        services.AddSingleton(_caseWorking);
        var migration = new OverseasSitesCorrectionMigration(
            services.BuildServiceProvider(),
            Options.Create(
                new OverseasSitesCorrectionOptions
                {
                    Enabled = true,
                    Apply = true,
                    SpotCheckConfirmedBy = Reviewer,
                }
            ),
            _logger
        );

        await migration.StartAsync(TestContext.Current.CancellationToken);
        await migration.ExecuteTask!;

        Logged(LogLevel.Error)
            .Should()
            .Equal(
                "RA-768 overseas sites correction failed before any application was processed: "
                    + "error=InvalidOperationException"
            );
        _logger.Entries.Should().NotContain(e => e.Message.Contains("secret"));
        await DidNotWriteAsync();
    }

    [Fact]
    public async Task Running_again_after_apply_changes_nothing()
    {
        var (workItemId, _) = await SeedAsync();
        await RunAsync(apply: true);
        // The Case Management service no longer holds the deselected site.
        _workItems[workItemId] = _workItems[workItemId] with
        {
            OverseasSites = [Held("ORS-1", "1")],
        };
        _caseWorking.ClearReceivedCalls();

        var summary = await RunAsync(apply: true);

        summary.Unchanged.Should().Be(1);
        summary.Updated.Should().Be(0);
        await DidNotWriteAsync();
    }

    [Fact]
    public async Task A_run_leaves_every_application_in_this_service_untouched()
    {
        await SeedAsync();
        var before = await ReadAllAsync();

        await RunAsync(apply: true);

        (await ReadAllAsync()).Should().BeEquivalentTo(before);
    }

    private async Task<List<AccreditationApplicationModel>> ReadAllAsync() =>
        await _factory
            .GetCollection<AccreditationApplicationModel>("accreditationApplications")
            .Find(FilterDefinition<AccreditationApplicationModel>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);

    [Fact]
    public void Every_outcome_has_its_log_text()
    {
        Enum.GetValues<OverseasSitesCorrectionMigration.Outcome>()
            .Select(OverseasSitesCorrectionMigration.OutcomeText)
            .Should()
            .OnlyHaveUniqueItems();
        var act = () =>
            OverseasSitesCorrectionMigration.OutcomeText(
                (OverseasSitesCorrectionMigration.Outcome)99
            );
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_shipped_appsettings_has_the_migration_off_and_logs_it_at_information()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

        var options = configuration
            .GetSection(OverseasSitesCorrectionOptions.SectionName)
            .Get<OverseasSitesCorrectionOptions>();
        options.Should().NotBeNull();
        options!.Enabled.Should().BeFalse();
        options.Apply.Should().BeFalse();
        options.SpotCheckConfirmedBy.Should().BeNullOrEmpty();
        // CDP defaults to Warning, so without this nothing appears in ext-test or prod.
        configuration[
            "Serilog:MinimumLevel:Override:EprRegisterEnrolBackend.AccreditationApplication.Startup.OverseasSitesCorrectionMigration"
        ]
            .Should()
            .Be("Information");
    }
}
