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
using RunResult = EprRegisterEnrolBackend.AccreditationApplication.Startup.DuplicateApplicationDeletionMigration.RunResult;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-788: runs against the real ephemeral mongod (a single-node replica set), so the transaction,
// its rollback and the guarded delete are exercised exactly as production runs them.
public sealed class DuplicateApplicationDeletionMigrationTests : IDisposable
{
    private const string ReExOrganisationId = "reex-500131";
    private const string PersonalName = "Jane Personal-Data";
    private const string PersonalEmail = "jane.personal@example.com";
    private const string OrganisationName = "Personal Plastics Ltd";

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

    private static AccreditationApplicationModel Application(
        ObjectId? id,
        ApplicationStatus status,
        int? orgId,
        string registrationId = Migration.RegistrationId,
        int year = Migration.AccreditationYear,
        MaterialType material = Migration.Material,
        string organisationId = ReExOrganisationId,
        long version = 3
    ) =>
        new()
        {
            Id = id ?? ObjectId.GenerateNewId(),
            OrganisationId = organisationId,
            OrgId = orgId,
            OrganisationName = OrganisationName,
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

    private static AccreditationApplicationModel Target(
        ApplicationStatus status = ApplicationStatus.Saved,
        int? orgId = null,
        string registrationId = Migration.RegistrationId,
        int year = Migration.AccreditationYear
    ) => Application(Migration.TargetId, status, orgId, registrationId, year);

    private static AccreditationApplicationModel Keeper(
        ApplicationStatus status = ApplicationStatus.Updated,
        int? orgId = Migration.OrgId,
        string organisationId = ReExOrganisationId
    ) => Application(Migration.KeeperId, status, orgId, organisationId: organisationId);

    private async Task SeedAsync(params AccreditationApplicationModel[] applications)
    {
        foreach (var application in applications)
            await _applications.InsertOneAsync(application, cancellationToken: Ct);
    }

    // The production pair, plus records that must never be touched: the same registration for
    // another year, and a Withdrawn record in the group (not live, so not counted).
    private async Task SeedProductionShapeAsync(int? targetOrgId = null)
    {
        await SeedAsync(
            Target(orgId: targetOrgId),
            Keeper(),
            Application(null, ApplicationStatus.Approved, Migration.OrgId, year: 2026),
            Application(null, ApplicationStatus.Withdrawn, Migration.OrgId)
        );
    }

    private Migration CreateMigration(bool enabled, bool apply = false, string? confirmedBy = null)
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
                }
            ),
            _logger
        );
    }

    private Migration Applying() => CreateMigration(true, apply: true, confirmedBy: "A Reviewer");

    private async Task<bool> ExistsAsync(ObjectId id) =>
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
        await SeedProductionShapeAsync();

        var result = await CreateMigration(enabled: false).RunAsync(Ct);

        result.Should().Be(RunResult.Disabled);
        (await ExistsAsync(Migration.TargetId)).Should().BeTrue();
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_logs_the_planned_delete_and_writes_nothing()
    {
        await SeedProductionShapeAsync();

        var result = await CreateMigration(enabled: true).RunAsync(Ct);

        result.Should().Be(RunResult.DryRun);
        (await CountAsync()).Should().Be(4);
        Messages
            .Should()
            .Contain(m =>
                m.StartsWith("RA-788 DRY RUN")
                && m.Contains("would delete application " + Migration.TargetId)
            );
    }

    [Fact]
    public async Task Apply_without_a_reviewer_stays_a_dry_run()
    {
        await SeedProductionShapeAsync();

        var result = await CreateMigration(enabled: true, apply: true, confirmedBy: " ")
            .RunAsync(Ct);

        result.Should().Be(RunResult.DryRun);
        (await ExistsAsync(Migration.TargetId)).Should().BeTrue();
        _logger
            .Entries.Should()
            .Contain(e =>
                e.LogLevel == LogLevel.Error && e.Message.Contains("SpotCheckConfirmedBy is not")
            );
    }

    // --- Success ---

    [Fact]
    public async Task Apply_deletes_only_the_target_and_a_second_run_is_a_no_op()
    {
        await SeedProductionShapeAsync();

        var first = await Applying().RunAsync(Ct);

        first.Should().Be(RunResult.Applied);
        (await ExistsAsync(Migration.TargetId)).Should().BeFalse();
        (await ExistsAsync(Migration.KeeperId)).Should().BeTrue();
        (await CountAsync()).Should().Be(3);
        Messages
            .Should()
            .ContainSingle(m => m.StartsWith("RA-788 APPLIED"))
            .Which.Should()
            .Contain("A Reviewer");

        var second = await Applying().RunAsync(Ct);

        second.Should().Be(RunResult.AlreadyDone);
        (await CountAsync()).Should().Be(3);
        Messages.Should().ContainSingle(m => m.StartsWith("RA-788 APPLIED"));
        Messages.Should().Contain(m => m.Contains("already deleted"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(Migration.OrgId)]
    public async Task A_backfilled_or_missing_target_orgId_passes(int? targetOrgId)
    {
        await SeedProductionShapeAsync(targetOrgId);

        var result = await Applying().RunAsync(Ct);

        result.Should().Be(RunResult.Applied);
        (await ExistsAsync(Migration.TargetId)).Should().BeFalse();
    }

    // --- Aborts ---

    public static TheoryData<string> TargetMismatches => ["status", "registration", "year", "orgId"];

    [Theory]
    [MemberData(nameof(TargetMismatches))]
    public async Task A_target_that_does_not_match_aborts(string mismatch)
    {
        await SeedAsync(
            mismatch switch
            {
                "status" => Target(status: ApplicationStatus.Submitted),
                "registration" => Target(registrationId: "68da9750c020f0e3dff99999"),
                "year" => Target(year: 2026),
                _ => Target(orgId: 500999),
            },
            Keeper()
        );

        await AssertAbortedAsync(Applying());
    }

    public static TheoryData<string> KeeperMismatches =>
        ["missing", "status", "organisation", "orgId"];

    [Theory]
    [MemberData(nameof(KeeperMismatches))]
    public async Task A_keeper_that_is_missing_or_does_not_match_aborts(string mismatch)
    {
        await SeedAsync(Target());
        switch (mismatch)
        {
            case "status":
                await SeedAsync(Keeper(status: ApplicationStatus.Saved));
                break;
            case "organisation":
                await SeedAsync(Keeper(organisationId: "reex-other"));
                break;
            case "orgId":
                await SeedAsync(Keeper(orgId: 500999));
                break;
        }

        await AssertAbortedAsync(Applying());
    }

    [Fact]
    public async Task A_third_live_application_in_the_group_aborts()
    {
        await SeedAsync(
            Target(),
            Keeper(),
            Application(null, ApplicationStatus.Saved, null)
        );

        await AssertAbortedAsync(Applying());
    }

    [Fact]
    public async Task A_missing_target_with_no_live_keeper_aborts()
    {
        await SeedAsync(Keeper(status: ApplicationStatus.Withdrawn));

        var result = await Applying().RunAsync(Ct);

        result.Should().Be(RunResult.Aborted);
        (await ExistsAsync(Migration.KeeperId)).Should().BeTrue();
    }

    private async Task AssertAbortedAsync(Migration migration)
    {
        var before = await CountAsync();

        var result = await migration.RunAsync(Ct);

        result.Should().Be(RunResult.Aborted);
        (await CountAsync()).Should().Be(before);
        Messages.Should().NotContain(m => m.StartsWith("RA-788 APPLIED"));
        _logger
            .Entries.Should()
            .Contain(e =>
                e.LogLevel == LogLevel.Error
                && e.Message.StartsWith("RA-788 APPLY: aborted, nothing has been written")
            );
    }

    // --- Rollbacks ---

    [Fact]
    public async Task A_target_edited_after_the_checks_rolls_back()
    {
        await SeedProductionShapeAsync();
        var migration = Applying();
        migration.BeforeDeleteForTest = async token =>
            await _applications.UpdateOneAsync(
                a => a.Id == Migration.TargetId,
                Builders<AccreditationApplicationModel>.Update.Inc(a => a.Version, 1),
                cancellationToken: token
            );

        var result = await migration.RunAsync(Ct);

        result.Should().Be(RunResult.RolledBack);
        (await ExistsAsync(Migration.TargetId)).Should().BeTrue();
        Messages.Should().Contain(m => m.Contains("target changed after the checks"));
    }

    [Fact]
    public async Task A_fault_after_the_delete_rolls_back()
    {
        await SeedProductionShapeAsync();
        var migration = Applying();
        migration.AfterDeleteForTest = _ => throw new InvalidOperationException("injected");

        var result = await migration.RunAsync(Ct);

        result.Should().Be(RunResult.RolledBack);
        (await ExistsAsync(Migration.TargetId)).Should().BeTrue();
        (await CountAsync()).Should().Be(4);
        Messages
            .Should()
            .Contain(m =>
                m.StartsWith("RA-788 APPLY: rolled back")
                && m.Contains(nameof(InvalidOperationException))
            );
        Messages.Should().NotContain(m => m.Contains("injected"));
    }

    // --- Logging ---

    [Fact]
    public async Task Every_line_is_prefixed_and_no_personal_data_is_logged()
    {
        await SeedProductionShapeAsync();

        await CreateMigration(enabled: true).RunAsync(Ct);
        await Applying().RunAsync(Ct);
        await Applying().RunAsync(Ct);

        _logger.Entries.Should().NotBeEmpty();
        Messages.Should().OnlyContain(m => m.StartsWith("RA-788 "));
        Messages
            .Should()
            .OnlyContain(m =>
                m.StartsWith("RA-788 DRY RUN")
                || m.StartsWith("RA-788 APPLY:")
                || m.StartsWith("RA-788 APPLIED:")
            );
        foreach (var personal in new[] { PersonalName, PersonalEmail, OrganisationName, ReExOrganisationId })
            Messages.Should().NotContain(m => m.Contains(personal));
    }

    // The failure is recorded with its mode on a dry run and an apply alike.
    [Theory]
    [InlineData(false, null, "RA-788 DRY RUN (nothing will be written): duplicate application deletion failed")]
    [InlineData(true, null, "RA-788 DRY RUN (nothing will be written): duplicate application deletion failed")]
    [InlineData(true, "A Reviewer", "RA-788 APPLY: duplicate application deletion failed")]
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
