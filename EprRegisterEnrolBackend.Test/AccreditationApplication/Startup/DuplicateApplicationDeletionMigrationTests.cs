using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
    private const string RegistrationId = "reg-id-1";
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
        string? registrationId = RegistrationId,
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
            RegistrationId = registrationId,
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

    // The year separates groups in the tests that seed several.
    private static AccreditationApplicationModel Candidate(int year = 2027) =>
        App(ApplicationStatus.Saved, null, year: year);

    private static AccreditationApplicationModel Keeper(int year = 2027) =>
        App(ApplicationStatus.Updated, 500131, year: year);

    private async Task SeedAsync(params AccreditationApplicationModel[] applications)
    {
        foreach (var application in applications)
            await _applications.InsertOneAsync(application, cancellationToken: Ct);
    }

    private Migration CreateMigration(
        bool enabled,
        bool apply = false,
        string? confirmedBy = null,
        int? expected = null,
        string? expectedIds = null
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
                    ExpectedApplicationIds = expectedIds,
                }
            ),
            _logger
        );
    }

    private Migration DryRun() => CreateMigration(true);

    // Apply, reviewed against exactly these candidates (count and ids).
    private Migration Applying(params AccreditationApplicationModel[] expected) =>
        ApplyingWith(expected.Length, string.Join(",", expected.Select(a => a.Id)));

    private Migration ApplyingWith(int? count, string? ids) =>
        CreateMigration(
            true,
            apply: true,
            confirmedBy: Reviewer,
            expected: count,
            expectedIds: ids
        );

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
        result.WouldDeleteRecords.Should().Be(1);
        result.DeletedRecords.Should().Be(0);
        (await CountAsync()).Should().Be(2);
        Messages
            .Should()
            .ContainSingle(m =>
                m.StartsWith("RA-788 DRY RUN")
                && m.Contains("material=Plastic year=2027")
                && m.Contains("outcome=would-delete")
                && m.Contains($"candidates=1 [{candidate.Id}(registration={Registration} status=Saved version=3")
                && m.Contains("createdAt=")
                && m.Contains("dateLastEdited=")
                && m.Contains($"keepers=1 [{keeper.Id}(registration={Registration} status=Updated hasOrgId=True)]")
            );
        Messages
            .Should()
            .Contain(m =>
                m.Contains("summary groups=1 candidates=1 candidate-ids=" + candidate.Id)
            )
            .And.Contain(m =>
                m.Contains("outcome would-delete-records=1 deleted-records=0")
            );
    }

    [Fact]
    public async Task The_summary_is_logged_before_the_group_detail_and_the_outcome_last()
    {
        await SeedAsync(Candidate(), Keeper());

        await DryRun().RunAsync(Ct);

        var lines = Messages.ToList();
        lines.Should().HaveCount(3);
        lines[0].Should().Contain("summary groups=1 candidates=1 candidate-ids=");
        lines[1].Should().Contain("group organisationId=");
        lines[2].Should().Contain("outcome would-delete-records=1");
    }

    [Fact]
    public async Task A_candidate_deleted_by_another_run_is_logged_as_such_not_as_an_error()
    {
        var gone = Candidate();
        await SeedAsync(gone, Keeper());
        var migration = Applying(gone);
        migration.BeforeDeleteForTest = async (_, token) =>
            await _applications.DeleteOneAsync(a => a.Id == gone.Id, token);

        var result = await migration.RunAsync(Ct);

        result.DeletedElsewhereGroups.Should().Be(1);
        result.RolledBackGroups.Should().Be(0);
        result.DeletedRecords.Should().Be(0);
        _logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
        Messages.Should().Contain(m => m.Contains("already deleted by another run"));
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
        var candidate = Candidate();
        await SeedAsync(candidate, Keeper());

        var result = await ApplyingWith(expected, candidate.Id.ToString()).RunAsync(Ct);

        result.Mode.Should().Be(RunMode.DryRun);
        result.DeletedRecords.Should().Be(0);
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

    // --- ExpectedApplicationIds: which records, not just how many ---

    [Fact]
    public async Task Apply_with_the_expected_ids_in_any_order_and_spacing_deletes()
    {
        var (c1, c2) = (Candidate(), Candidate());
        await SeedAsync(c1, c2, Keeper());

        var result = await ApplyingWith(2, $" {c2.Id} , {c1.Id},").RunAsync(Ct);

        result.Mode.Should().Be(RunMode.Apply);
        result.DeletedRecords.Should().Be(2);
        (await CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Apply_with_a_swapped_id_and_the_same_count_deletes_nothing()
    {
        // One candidate was reviewed; a different one is found now. The count still matches.
        var reviewed = Candidate();
        var found = Candidate();
        await SeedAsync(found, Keeper());

        var result = await ApplyingWith(1, reviewed.Id.ToString()).RunAsync(Ct);

        result.Mode.Should().Be(RunMode.DryRun);
        result.DeletedRecords.Should().Be(0);
        (await ExistsAsync(found.Id)).Should().BeTrue();
        (await CountAsync()).Should().Be(2);
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Error
                && e.Message.Contains($"found but not expected: [{found.Id}]")
                && e.Message.Contains($"expected but not found: [{reviewed.Id}]")
            );
    }

    [Fact]
    public async Task Apply_with_an_extra_expected_id_deletes_nothing()
    {
        var candidate = Candidate();
        await SeedAsync(candidate, Keeper());

        var result = await ApplyingWith(1, $"{candidate.Id},{ObjectId.GenerateNewId()}")
            .RunAsync(Ct);

        result.Mode.Should().Be(RunMode.DryRun);
        (await CountAsync()).Should().Be(2);
        _logger.Entries.Should().Contain(e => e.LogLevel == LogLevel.Error);
    }

    [Theory]
    [InlineData(null, "ExpectedApplicationIds is not")]
    [InlineData(" , ", "ExpectedApplicationIds is not")]
    [InlineData("not-an-id", "not an application id")]
    public async Task Apply_with_missing_or_invalid_expected_ids_deletes_nothing(
        string? ids,
        string expectedError
    )
    {
        await SeedAsync(Candidate(), Keeper());

        var result = await ApplyingWith(1, ids).RunAsync(Ct);

        result.Mode.Should().Be(RunMode.DryRun);
        (await CountAsync()).Should().Be(2);
        _logger
            .Entries.Should()
            .Contain(e => e.LogLevel == LogLevel.Error && e.Message.Contains(expectedError));
    }

    [Fact]
    public async Task Apply_with_no_candidates_is_already_done_without_an_error()
    {
        await SeedAsync(Keeper(), Keeper());

        var result = await ApplyingWith(1, ObjectId.GenerateNewId().ToString()).RunAsync(Ct);

        result.Mode.Should().Be(RunMode.Apply);
        _logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
        Messages.Should().Contain(m => m.Contains("no candidates found, so nothing to delete"));
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
        ["organisationId", "organisationName", "registrationId", "material", "year"];

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
                "registrationId" => App(ApplicationStatus.Updated, 500131, registrationId: "reg-id-2"),
                "material" => App(ApplicationStatus.Updated, 500131, material: MaterialType.Glass),
                // The same organisation carries across years: an abandoned draft for one year is
                // not a duplicate of a live application for another.
                _ => App(ApplicationStatus.Updated, 500131, year: 2026),
            }
        );

        var result = await Applying().RunAsync(Ct);

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

        var result = await Applying().RunAsync(Ct);

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
            App(ApplicationStatus.Saved, null, organisationId: blank),
            App(ApplicationStatus.Saved, null, registrationId: blank)
        );

        var result = await DryRun().RunAsync(Ct);

        result.Groups.Should().Be(1);
        result.Candidates.Should().Be(1);
        result.UngroupedMissingKey.Should().Be(3);
    }

    [Theory]
    [InlineData("REG-0002")]
    [InlineData(null)]
    [InlineData("")]
    public async Task The_registration_reference_is_not_a_grouping_attribute(string? other)
    {
        var candidate = Candidate();
        await SeedAsync(candidate, App(ApplicationStatus.Updated, 500131, registration: other));

        var result = await Applying(candidate).RunAsync(Ct);

        result.Groups.Should().Be(1);
        result.DeletedRecords.Should().Be(1);
        (await ExistsAsync(candidate.Id)).Should().BeFalse();
    }

    // --- Outcomes ---

    [Fact]
    public async Task A_keeper_plus_one_candidate_deletes_the_candidate_and_a_second_run_finds_none()
    {
        var candidate = Candidate();
        var keeper = Keeper();
        await SeedAsync(candidate, keeper);

        var first = await Applying(candidate).RunAsync(Ct);

        first.Mode.Should().Be(RunMode.Apply);
        first.DeletedRecords.Should().Be(1);
        first.RolledBackGroups.Should().Be(0);
        (await ExistsAsync(candidate.Id)).Should().BeFalse();
        (await ExistsAsync(keeper.Id)).Should().BeTrue();
        Messages
            .Should()
            .ContainSingle(m => m.StartsWith("RA-788 APPLIED"))
            .Which.Should()
            .Contain(candidate.Id.ToString())
            .And.NotContain(Reviewer);

        var second = await Applying().RunAsync(Ct);

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
        var (c1, c2) = (Candidate(), Candidate());
        await SeedAsync(c1, c2, keeper);

        var result = await Applying(c1, c2).RunAsync(Ct);

        result.DeletedRecords.Should().Be(2);
        (await CountAsync()).Should().Be(1);
        (await ExistsAsync(keeper.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Only_candidates_is_skipped_no_keeper()
    {
        await SeedAsync(Candidate(), Candidate());

        var result = await Applying().RunAsync(Ct);

        result.SkippedNoKeeper.Should().Be(1);
        result.Candidates.Should().Be(0);
        (await CountAsync()).Should().Be(2);
        Messages.Should().Contain(m => m.Contains("outcome=skipped-no-keeper"));
    }

    [Fact]
    public async Task Only_keepers_is_skipped_no_candidate()
    {
        await SeedAsync(Keeper(), Keeper());

        var result = await Applying().RunAsync(Ct);

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

        var result = await Applying().RunAsync(Ct);

        result.Candidates.Should().Be(0);
        result.SkippedNoCandidate.Should().Be(1);
        (await CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_Saved_record_with_an_orgId_or_a_Submitted_record_is_kept_beside_a_candidate()
    {
        var candidate = Candidate();
        var savedWithOrgId = App(ApplicationStatus.Saved, 500131);
        var submitted = App(ApplicationStatus.Submitted, null);
        await SeedAsync(candidate, savedWithOrgId, submitted);

        var result = await Applying(candidate).RunAsync(Ct);

        result.DeletedRecords.Should().Be(1);
        (await ExistsAsync(candidate.Id)).Should().BeFalse();
        (await ExistsAsync(savedWithOrgId.Id)).Should().BeTrue();
        (await ExistsAsync(submitted.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task Several_groups_are_handled_independently()
    {
        var first = Candidate();
        var other = Candidate(2028);
        await SeedAsync(
            first,
            Keeper(),
            other,
            Keeper(2028),
            Candidate(2029),
            Candidate(2029),
            Keeper(2030),
            Keeper(2030)
        );

        var result = await Applying(first, other).RunAsync(Ct);

        result.Groups.Should().Be(4);
        result.DeletedRecords.Should().Be(2);
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
        var untouched = Candidate(2028);
        await SeedAsync(edited, Keeper(), untouched, Keeper(2028));
        var migration = Applying(edited, untouched);
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

        result.RolledBackGroups.Should().Be(1);
        result.DeletedRecords.Should().Be(1);
        (await ExistsAsync(edited.Id)).Should().BeTrue();
        (await ExistsAsync(untouched.Id)).Should().BeFalse();
        Messages.Should().Contain(m => m.Contains("removed or changed after the scan"));
    }

    [Fact]
    public async Task A_keeper_withdrawn_inside_the_transaction_rolls_that_group_back()
    {
        var candidate = Candidate();
        var keeper = Keeper();
        var other = Candidate(2028);
        await SeedAsync(candidate, keeper, other, Keeper(2028));
        var migration = Applying(candidate, other);
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

        result.RolledBackGroups.Should().Be(1);
        result.DeletedRecords.Should().Be(1);
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
        var migration = Applying(candidate);
        migration.AfterDeleteForTest = async (_, session, token) =>
            await _applications.InsertOneAsync(session, Candidate(), cancellationToken: token);

        var result = await migration.RunAsync(Ct);

        result.RolledBackGroups.Should().Be(1);
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
        var fine = Candidate(2028);
        await SeedAsync(faulty, Keeper(), fine, Keeper(2028));
        var migration = Applying(faulty, fine);
        migration.AfterDeleteForTest = (ids, _, _) =>
            ids.Contains(faulty.Id!.Value)
                ? throw new InvalidOperationException("injected")
                : Task.CompletedTask;

        var result = await migration.RunAsync(Ct);

        result.RolledBackGroups.Should().Be(1);
        result.DeletedRecords.Should().Be(1);
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
        var migration = Applying(candidate);
        migration.AfterDeleteForTest = (_, _, _) => throw new InvalidOperationException("injected");
        migration.BeforeVerifyForTest = _ => throw new TimeoutException("injected re-read");

        var result = await migration.RunAsync(Ct);

        result.OutcomeUnknownGroups.Should().Be(1);
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
        var migration = Applying(candidate);
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
    public async Task Every_line_is_prefixed_states_its_mode_and_logs_no_applicant_or_reviewer_data()
    {
        var candidate = Candidate();
        await SeedAsync(candidate, Keeper(), Candidate(2028));

        await DryRun().RunAsync(Ct);
        await CreateMigration(true, apply: true, confirmedBy: Reviewer, expected: 5).RunAsync(Ct);
        await Applying(candidate).RunAsync(Ct);
        await Applying().RunAsync(Ct);

        _logger.Entries.Should().NotBeEmpty();
        Messages
            .Should()
            .OnlyContain(m =>
                m.StartsWith("RA-788 DRY RUN")
                || m.StartsWith("RA-788 APPLY:")
                || m.StartsWith("RA-788 APPLIED:")
            );
        foreach (var personal in new[] { PersonalName, PersonalEmail, Reviewer })
            Messages.Should().NotContain(m => m.Contains(personal));
    }

    [Fact]
    public async Task Each_group_line_names_the_organisation_so_the_deletion_can_be_reviewed()
    {
        await SeedAsync(Candidate(), Keeper());

        await DryRun().RunAsync(Ct);

        Messages
            .Should()
            .ContainSingle(m =>
                m.Contains("group organisationId=" + ReExOrganisationId + " ")
                && m.Contains("organisationName=" + OrganisationName + " registrationId=")
            );
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

    // A setting that fails to bind (ExpectedDeletions=one) makes options.Value throw. That must be
    // logged, not escape ExecuteAsync and stop the host.
    [Fact]
    public async Task ExecuteAsync_logs_settings_that_fail_to_bind_instead_of_throwing()
    {
        var migration = new Migration(
            new ServiceCollection().BuildServiceProvider(),
            new UnbindableOptions(),
            _logger
        );

        await migration.StartAsync(Ct);
        await (migration.ExecuteTask ?? Task.CompletedTask);

        migration.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        _logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Which.Message.Should()
            .StartWith("RA-788 DRY RUN")
            .And.Contain("settings could not be read")
            .And.Contain("error=" + nameof(InvalidOperationException))
            .And.NotContain("Failed to convert");
    }

    [Fact]
    public async Task Real_binding_of_a_non_numeric_ExpectedDeletions_throws_on_read()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Migrations:Ra788DuplicateApplicationDeletion:ExpectedDeletions"] = "one",
                }
            )
            .Build();
        var services = new ServiceCollection();
        services.Configure<DuplicateApplicationDeletionOptions>(
            configuration.GetSection(DuplicateApplicationDeletionOptions.SectionName)
        );
        var options = services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<DuplicateApplicationDeletionOptions>>();
        var migration = new Migration(services.BuildServiceProvider(), options, _logger);

        await migration.StartAsync(Ct);
        await (migration.ExecuteTask ?? Task.CompletedTask);

        _logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Which.Message.Should()
            .Contain("settings could not be read");
    }

    private sealed class UnbindableOptions : IOptions<DuplicateApplicationDeletionOptions>
    {
        public DuplicateApplicationDeletionOptions Value =>
            throw new InvalidOperationException("Failed to convert configuration value 'one'");
    }
}
