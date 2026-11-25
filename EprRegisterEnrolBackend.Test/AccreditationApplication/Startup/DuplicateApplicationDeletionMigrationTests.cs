using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Migration = EprRegisterEnrolBackend.AccreditationApplication.Startup.DuplicateApplicationDeletionMigration;
using RunMode = EprRegisterEnrolBackend.AccreditationApplication.Startup.DuplicateApplicationDeletionMigration.RunMode;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-788: runs against the real ephemeral mongod (a single-node replica set), so the transaction,
// its rollback and the guarded delete are exercised exactly as production runs them.
public sealed class DuplicateApplicationDeletionMigrationTests : IDisposable
{
    private const string ReExOrganisationId = "reex-500131";
    private const string PersonalName = "Jane Personal-Data";
    private const string PersonalEmail = "jane.personal@example.com";
    private const string OrganisationName = "Personal Plastics Ltd";
    private const string Registration = "REG-0001";
    private const string Reviewer = "A Reviewer";

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly IMongoCollection<AccreditationApplicationModel> _applications;
    private readonly CapturingLogger<Migration> _logger = new();

    public DuplicateApplicationDeletionMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra788");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _applications = _factory.GetCollection<AccreditationApplicationModel>(
            DomainModelCollections.LegacyApplications
        );
        // Create the collection up front: a transaction cannot create one on an older server.
        _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .CreateCollection(DomainModelCollections.LegacyApplications);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccreditationApplicationModel App(
        ApplicationStatus status,
        int? orgId,
        string? organisationId = ReExOrganisationId,
        string? organisationName = OrganisationName,
        string? registration = Registration,
        MaterialType material = MaterialType.Plastic,
        int year = 2027,
        long version = 3
    ) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = organisationId!,
            OrgId = orgId,
            OrganisationName = organisationName,
            RegistrationReference = registration,
            Year = year,
            MaterialType = material,
            ApplicationStatus = status,
            Version = version,
            SubmitterContactDetails = new SubmitterContactDetailsModel
            {
                FullName = PersonalName,
                Email = PersonalEmail,
            },
        };

    private static AccreditationApplicationModel Candidate(
        string? registration = Registration,
        int year = 2027
    ) => App(ApplicationStatus.Saved, null, registration: registration, year: year);

    private static AccreditationApplicationModel Keeper(
        string? registration = Registration,
        int year = 2027
    ) => App(ApplicationStatus.Updated, 500131, registration: registration, year: year);

    private async Task SeedAsync(params AccreditationApplicationModel[] applications)
    {
        foreach (var application in applications)
            await _applications.InsertOneAsync(application, cancellationToken: Ct);
    }

    private Migration CreateMigration(
        bool enabled,
        bool apply = false,
        string? confirmedBy = null,
        int? expected = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMongoDbClientFactory>(_factory);
        return new Migration(
            services.BuildServiceProvider(),
            Options.Create(
                new DuplicateApplicationDeletionOptions
                {
                    Enabled = enabled,
                    Apply = apply,
                    SpotCheckConfirmedBy = confirmedBy,
                    ExpectedDeletions = expected,
                }
            ),
            _logger
        );
    }

    private Migration DryRun() => CreateMigration(true);

    private Migration Applying(int expected) =>
        CreateMigration(true, apply: true, confirmedBy: Reviewer, expected: expected);

    private async Task<bool> ExistsAsync(ObjectId? id) =>
        await _applications.CountDocumentsAsync(a => a.Id == id, cancellationToken: Ct) == 1;

    private async Task<long> CountAsync() =>
        await _applications.CountDocumentsAsync(
            FilterDefinition<AccreditationApplicationModel>.Empty,
            cancellationToken: Ct
        );

    private IEnumerable<string> Messages => _logger.Entries.Select(e => e.Message);

    // --- Modes ---

    [Fact]
    public async Task Disabled_reads_and_writes_nothing()
    {
        await SeedAsync(Candidate(), Keeper());

        var result = await CreateMigration(enabled: false).RunAsync(Ct);

        result.Mode.Should().Be(RunMode.Disabled);
        (await CountAsync()).Should().Be(2);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_logs_the_plan_and_a_summary_and_writes_nothing()
    {
        var candidate = Candidate();
        var keeper = Keeper();
        await SeedAsync(candidate, keeper);

        var result = await DryRun().RunAsync(Ct);

        result.Mode.Should().Be(RunMode.DryRun);
        result.Groups.Should().Be(1);
        result.Candidates.Should().Be(1);
        result.WouldDelete.Should().Be(1);
        result.Deleted.Should().Be(0);
        (await CountAsync()).Should().Be(2);
        Messages
            .Should()
            .Contain(m =>
                m.StartsWith("RA-788 DRY RUN")
                && m.Contains($"candidate {candidate.Id} status=Saved version=3")
                && m.Contains("createdAt=")
                && m.Contains("dateLastEdited=")
            )
            .And.Contain(m =>
                m.StartsWith("RA-788 DRY RUN")
                && m.Contains($"keeper {keeper.Id} status=Updated hasOrgId=True")
            )
            .And.Contain(m =>
                m.Contains("group registration=REG-0001 material=Plastic year=2027")
                && m.Contains("outcome=would-delete")
            )
            .And.Contain(m => m.Contains("summary groups=1 candidates=1 would-delete=1 deleted=0"));
    }

    [Fact]
    public async Task Apply_without_a_reviewer_is_a_dry_run()
    {
        await SeedAsync(Candidate(), Keeper());

        var result = await CreateMigration(true, apply: true, confirmedBy: " ", expected: 1)
            .RunAsync(Ct);

        result.Mode.Should().Be(RunMode.DryRun);
        (await CountAsync()).Should().Be(2);
        _logger
            .Entries.Should()
            .Contain(e =>
                e.LogLevel == LogLevel.Error && e.Message.Contains("SpotCheckConfirmedBy is not")
            );
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Apply_with_a_missing_or_wrong_ExpectedDeletions_is_a_dry_run_with_an_error(
        int? expected
    )
    {
        await SeedAsync(Candidate(), Keeper());

        var result = await CreateMigration(true, apply: true, confirmedBy: Reviewer, expected)
            .RunAsync(Ct);

        result.Mode.Should().Be(RunMode.DryRun);
        result.Deleted.Should().Be(0);
        (await CountAsync()).Should().Be(2);
        _logger
            .Entries.Should()
            .Contain(e =>
                e.LogLevel == LogLevel.Error
                && e.Message.StartsWith("RA-788 DRY RUN")
                && e.Message.Contains("ExpectedDeletions=")
                && e.Message.Contains("1 candidates found this run")
            );
    }

    // --- Grouping ---

    [Fact]
    public async Task A_match_on_all_five_fields_groups()
    {
        await SeedAsync(Candidate(), Keeper());

        var result = await DryRun().RunAsync(Ct);

        result.Groups.Should().Be(1);
    }

    public static TheoryData<string> Differences =>
        ["organisationId", "organisationName", "material", "registration", "year"];

    [Theory]
    [MemberData(nameof(Differences))]
    public async Task A_difference_in_any_one_field_does_not_group(string field)
    {
        await SeedAsync(
            Candidate(),
            field switch
            {
                "organisationId" => App(ApplicationStatus.Updated, 500131, organisationId: "reex-2"),
                "organisationName" => App(
                    ApplicationStatus.Updated,
                    500131,
                    organisationName: "Other Ltd"
                ),
                "material" => App(ApplicationStatus.Updated, 500131, material: MaterialType.Glass),
                "registration" => App(ApplicationStatus.Updated, 500131, registration: "REG-0002"),
                // The same registration carries across years: an abandoned draft for one year is
                // not a duplicate of a live application for another.
                _ => App(ApplicationStatus.Updated, 500131, year: 2026),
            }
        );

        var result = await Applying(0).RunAsync(Ct);

        result.Groups.Should().Be(0);
        (await CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task The_organisation_name_match_is_exact()
    {
        await SeedAsync(
            Candidate(),
            App(ApplicationStatus.Updated, 500131, organisationName: OrganisationName.ToUpperInvariant())
        );

        var result = await DryRun().RunAsync(Ct);

        result.Groups.Should().Be(0);
    }

    [Fact]
    public async Task Withdrawn_records_are_ignored()
    {
        // The Withdrawn record is neither grouped, kept nor deleted: this leaves a group of one.
        var withdrawn = App(ApplicationStatus.Withdrawn, null);
        await SeedAsync(Candidate(), withdrawn);

        var result = await Applying(0).RunAsync(Ct);

        result.Groups.Should().Be(0);
        (await ExistsAsync(withdrawn.Id)).Should().BeTrue();
        (await CountAsync()).Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task A_null_or_empty_key_field_leaves_the_record_ungrouped(string? blank)
    {
        await SeedAsync(
            Candidate(),
            Keeper(),
            App(ApplicationStatus.Saved, null, organisationName: blank),
            App(ApplicationStatus.Saved, null, registration: blank),
            App(ApplicationStatus.Saved, null, organisationId: blank)
        );

        var result = await DryRun().RunAsync(Ct);

        result.Groups.Should().Be(1);
        result.Candidates.Should().Be(1);
        result.UngroupedMissingKey.Should().Be(3);
    }

    // --- Outcomes ---

    [Fact]
    public async Task A_keeper_plus_one_candidate_deletes_the_candidate_and_a_second_run_finds_none()
    {
        var candidate = Candidate();
        var keeper = Keeper();
        await SeedAsync(candidate, keeper);

        var first = await Applying(1).RunAsync(Ct);

        first.Mode.Should().Be(RunMode.Apply);
        first.Deleted.Should().Be(1);
        first.RolledBack.Should().Be(0);
        (await ExistsAsync(candidate.Id)).Should().BeFalse();
        (await ExistsAsync(keeper.Id)).Should().BeTrue();
        Messages
            .Should()
            .ContainSingle(m => m.StartsWith("RA-788 APPLIED"))
            .Which.Should()
            .Contain(candidate.Id.ToString())
            .And.NotContain(Reviewer);

        var second = await Applying(0).RunAsync(Ct);

        second.Mode.Should().Be(RunMode.Apply);
        second.Candidates.Should().Be(0);
        second.Groups.Should().Be(0);
        (await CountAsync()).Should().Be(1);
        Messages.Should().ContainSingle(m => m.StartsWith("RA-788 APPLIED"));
    }

    [Fact]
    public async Task A_keeper_plus_two_candidates_deletes_both()
    {
        var keeper = Keeper();
        await SeedAsync(Candidate(), Candidate(), keeper);

        var result = await Applying(2).RunAsync(Ct);

        result.Deleted.Should().Be(2);
        (await CountAsync()).Should().Be(1);
        (await ExistsAsync(keeper.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Only_candidates_is_skipped_no_keeper()
    {
        await SeedAsync(Candidate(), Candidate());

        var result = await Applying(0).RunAsync(Ct);

        result.SkippedNoKeeper.Should().Be(1);
        result.Candidates.Should().Be(0);
        (await CountAsync()).Should().Be(2);
        Messages.Should().Contain(m => m.Contains("outcome=skipped-no-keeper"));
    }

    [Fact]
    public async Task Only_keepers_is_skipped_no_candidate()
    {
        await SeedAsync(Keeper(), Keeper());

        var result = await Applying(0).RunAsync(Ct);

        result.SkippedNoCandidate.Should().Be(1);
        (await CountAsync()).Should().Be(2);
        Messages.Should().Contain(m => m.Contains("outcome=skipped-no-candidate"));
    }

    [Fact]
    public async Task A_record_with_an_orgId_or_a_status_other_than_Saved_is_a_keeper()
    {
        // Saved with an orgId, and Submitted without one: neither is a candidate, so this group
        // has only keepers.
        await SeedAsync(
            App(ApplicationStatus.Saved, 500131),
            App(ApplicationStatus.Submitted, null)
        );

        var result = await Applying(0).RunAsync(Ct);

        result.Candidates.Should().Be(0);
        result.SkippedNoCandidate.Should().Be(1);
        (await CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Several_groups_are_handled_independently()
    {
        var other = Candidate("REG-0002");
        await SeedAsync(
            Candidate(),
            Keeper(),
            other,
            Keeper("REG-0002"),
            Candidate("REG-0003"),
            Candidate("REG-0003"),
            Keeper("REG-0004"),
            Keeper("REG-0004")
        );

        var result = await Applying(2).RunAsync(Ct);

        result.Groups.Should().Be(4);
        result.Deleted.Should().Be(2);
        result.SkippedNoKeeper.Should().Be(1);
        result.SkippedNoCandidate.Should().Be(1);
        (await CountAsync()).Should().Be(6);
        (await ExistsAsync(other.Id)).Should().BeFalse();
    }

    // --- Rollbacks: that group is untouched and the others still go ahead ---

    [Fact]
    public async Task A_candidate_edited_between_scan_and_delete_rolls_that_group_back()
    {
        var edited = Candidate();
        var untouched = Candidate("REG-0002");
        await SeedAsync(edited, Keeper(), untouched, Keeper("REG-0002"));
        var migration = Applying(2);
        migration.BeforeDeleteForTest = async (ids, token) =>
        {
            if (ids.Contains(edited.Id!.Value))
                await _applications.UpdateOneAsync(
                    a => a.Id == edited.Id,
                    Builders<AccreditationApplicationModel>.Update.Inc(a => a.Version, 1),
                    cancellationToken: token
                );
        };

        var result = await migration.RunAsync(Ct);

        result.RolledBack.Should().Be(1);
        result.Deleted.Should().Be(1);
        (await ExistsAsync(edited.Id)).Should().BeTrue();
        (await ExistsAsync(untouched.Id)).Should().BeFalse();
        Messages.Should().Contain(m => m.Contains("removed or changed after the scan"));
    }

    [Fact]
    public async Task A_keeper_withdrawn_inside_the_transaction_rolls_that_group_back()
    {
        var candidate = Candidate();
        var keeper = Keeper();
        var other = Candidate("REG-0002");
        await SeedAsync(candidate, keeper, other, Keeper("REG-0002"));
        var migration = Applying(2);
        migration.AfterDeleteForTest = async (ids, session, token) =>
        {
            if (ids.Contains(candidate.Id!.Value))
                await _applications.UpdateOneAsync(
                    session,
                    a => a.Id == keeper.Id,
                    Builders<AccreditationApplicationModel>.Update.Set(
                        a => a.ApplicationStatus,
                        ApplicationStatus.Withdrawn
                    ),
                    cancellationToken: token
                );
        };

        var result = await migration.RunAsync(Ct);

        result.RolledBack.Should().Be(1);
        result.Deleted.Should().Be(1);
        (await ExistsAsync(candidate.Id)).Should().BeTrue();
        (await _applications.Find(a => a.Id == keeper.Id).SingleAsync(Ct))
            .ApplicationStatus.Should()
            .Be(ApplicationStatus.Updated);
        (await ExistsAsync(other.Id)).Should().BeFalse();
        Messages
            .Should()
            .Contain(m =>
                m.StartsWith("RA-788 APPLY: rolled back")
                && m.Contains("reason=a keeper is no longer live")
            );
    }

    [Fact]
    public async Task A_group_not_left_with_only_its_keepers_rolls_back()
    {
        var candidate = Candidate();
        await SeedAsync(candidate, Keeper());
        var migration = Applying(1);
        migration.AfterDeleteForTest = async (_, session, token) =>
            await _applications.InsertOneAsync(session, Candidate(), cancellationToken: token);

        var result = await migration.RunAsync(Ct);

        result.RolledBack.Should().Be(1);
        (await ExistsAsync(candidate.Id)).Should().BeTrue();
        (await CountAsync()).Should().Be(2);
        Messages
            .Should()
            .Contain(m =>
                m.Contains("reason=the group would hold 2 live applications; expected 1")
            );
    }

    [Fact]
    public async Task A_fault_after_a_delete_rolls_that_group_back_and_the_others_go_ahead()
    {
        var faulty = Candidate();
        var fine = Candidate("REG-0002");
        await SeedAsync(faulty, Keeper(), fine, Keeper("REG-0002"));
        var migration = Applying(2);
        migration.AfterDeleteForTest = (ids, _, _) =>
            ids.Contains(faulty.Id!.Value)
                ? throw new InvalidOperationException("injected")
                : Task.CompletedTask;

        var result = await migration.RunAsync(Ct);

        result.RolledBack.Should().Be(1);
        result.Deleted.Should().Be(1);
        (await ExistsAsync(faulty.Id)).Should().BeTrue();
        (await ExistsAsync(fine.Id)).Should().BeFalse();
        Messages
            .Should()
            .Contain(m =>
                m.StartsWith("RA-788 APPLY: rolled back") && m.Contains("reason=" + nameof(InvalidOperationException))
            );
        Messages.Should().NotContain(m => m.Contains("injected"));
    }

    // An error that is not a failed check, then a failed re-read: what happened is unknown, so it
    // is neither reported as rolled back nor as applied.
    [Fact]
    public async Task An_error_with_a_failed_re_read_reports_the_outcome_unknown()
    {
        var candidate = Candidate();
        await SeedAsync(candidate, Keeper());
        var migration = Applying(1);
        migration.AfterDeleteForTest = (_, _, _) => throw new InvalidOperationException("injected");
        migration.BeforeVerifyForTest = _ => throw new TimeoutException("injected re-read");

        var result = await migration.RunAsync(Ct);

        result.OutcomeUnknown.Should().Be(1);
        _logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error && e.Message.Contains("outcome unknown"))
            .Which.Message.Should()
            .Contain(nameof(InvalidOperationException))
            .And.Contain(nameof(TimeoutException))
            .And.Contain(candidate.Id.ToString());
        Messages.Should().NotContain(m => m.Contains("rolled back"));
        Messages.Should().NotContain(m => m.StartsWith("RA-788 APPLIED"));
    }

    // Stopping part-way through a delete is logged, not lost, and still stops the run.
    [Fact]
    public async Task Cancellation_during_the_delete_reports_the_outcome_unknown()
    {
        var candidate = Candidate();
        await SeedAsync(candidate, Keeper());
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var migration = Applying(1);
        migration.AfterDeleteForTest = (_, _, _) =>
        {
            cts.Cancel();
            return Task.CompletedTask;
        };

        var run = () => migration.RunAsync(cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        (await ExistsAsync(candidate.Id)).Should().BeTrue();
        _logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error && e.Message.Contains("outcome unknown"))
            .Which.Message.Should()
            .Contain("the run was cancelled during the delete");
    }

    // --- Logging ---

    [Fact]
    public async Task Every_line_is_prefixed_states_its_mode_and_logs_no_personal_data()
    {
        await SeedAsync(Candidate(), Keeper(), Candidate("REG-0002"));

        await DryRun().RunAsync(Ct);
        await CreateMigration(true, apply: true, confirmedBy: Reviewer, expected: 5).RunAsync(Ct);
        await Applying(1).RunAsync(Ct);
        await Applying(0).RunAsync(Ct);

        _logger.Entries.Should().NotBeEmpty();
        Messages
            .Should()
            .OnlyContain(m =>
                m.StartsWith("RA-788 DRY RUN")
                || m.StartsWith("RA-788 APPLY:")
                || m.StartsWith("RA-788 APPLIED:")
            );
        foreach (
            var personal in new[]
            {
                PersonalName,
                PersonalEmail,
                OrganisationName,
                ReExOrganisationId,
                Reviewer,
            }
        )
            Messages.Should().NotContain(m => m.Contains(personal));
    }

    // The failure is recorded with its mode on a dry run and an apply alike.
    [Theory]
    [InlineData(false, null, "RA-788 DRY RUN (nothing will be written): duplicate application deletion failed")]
    [InlineData(true, null, "RA-788 DRY RUN (nothing will be written): duplicate application deletion failed")]
    [InlineData(true, Reviewer, "RA-788 APPLY: duplicate application deletion failed")]
    public async Task ExecuteAsync_logs_a_failure_with_its_mode_instead_of_throwing(
        bool apply,
        string? confirmedBy,
        string expected
    )
    {
        // No IMongoDbClientFactory registered: resolving it throws.
        var migration = new Migration(
            new ServiceCollection().BuildServiceProvider(),
            Options.Create(
                new DuplicateApplicationDeletionOptions
                {
                    Enabled = true,
                    Apply = apply,
                    SpotCheckConfirmedBy = confirmedBy,
                }
            ),
            _logger
        );

        await migration.StartAsync(Ct);
        await (migration.ExecuteTask ?? Task.CompletedTask);

        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Error && e.Message.Contains("deletion failed")
            )
            .Which.Message.Should()
            .StartWith(expected)
            .And.Contain("error=");
    }
}