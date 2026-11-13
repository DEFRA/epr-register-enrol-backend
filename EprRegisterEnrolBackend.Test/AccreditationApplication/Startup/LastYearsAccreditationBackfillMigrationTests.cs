using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Migration = EprRegisterEnrolBackend.AccreditationApplication.Startup.LastYearsAccreditationBackfillMigration;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-763: runs against the real ephemeral mongod, with rows from the real public register, so the
// guarded updates and the matching are exercised exactly as production runs them.
public sealed class LastYearsAccreditationBackfillMigrationTests : IDisposable
{
    // Egger (UK) Limited: accredited in 2026, with a different sequence from its registration.
    private const string EggerRegistration = "R26ER5000270036WO";
    private const string EggerAccreditation = "A26ER5000270063WO";

    // DTS Trading LTD: registered only in 2026.
    private const string DtsRegistration = "R26ER5002370119PL";

    // GAP Alternative Fuels: an exporter.
    private const string GapRegistration = "R26WX5003080015PL";
    private const string GapAccreditation = "A26WX5003080014PL";

    // Clearfield Envirotech: issued a number derived from its registration.
    private const string ClearfieldRegistration = "R26ER5003240079PL";
    private const string ClearfieldAccreditation = "A26ER5003240164PL";
    private const string ClearfieldIncorrect = "A27ER5003240079PL";
    private const string ClearfieldCorrected = "A27ER5003240164PL";

    // Held back: the published accreditation number names another org.
    private const string JpRecyclingRegistration = "R26WR5003020027PL";

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _persistence;
    private readonly IMongoCollection<BsonDocument> _raw;
    private readonly CapturingLogger<Migration> _logger = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public LastYearsAccreditationBackfillMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra763");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );
        _raw = _factory.GetCollection<BsonDocument>("accreditationApplications");
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static AccreditationApplicationModel Application(
        string? registration,
        int? orgId,
        MaterialType material = MaterialType.Plastic,
        bool isExporter = false,
        int year = 2027,
        string? accreditationReference = null,
        ApplicationStatus status = ApplicationStatus.Submitted
    ) =>
        new()
        {
            OrganisationId = $"reex-{orgId}",
            OrgId = orgId,
            Year = year,
            MaterialType = material,
            IsExporter = isExporter,
            ApplicationStatus = status,
            ApplicationReference = $"APP-{Guid.NewGuid():N}",
            RegistrationReference = registration,
            AccreditationReference = accreditationReference,
        };

    private static AccreditationApplicationModel Egger() =>
        Application(EggerRegistration, 500027, MaterialType.Wood);

    private static AccreditationApplicationModel Clearfield(string? issued = ClearfieldIncorrect) =>
        Application(
            ClearfieldRegistration,
            500324,
            accreditationReference: issued,
            status: issued is null ? ApplicationStatus.Submitted : ApplicationStatus.Approved
        );

    private async Task<ObjectId> SeedAsync(AccreditationApplicationModel application)
    {
        await _persistence.CreateAsync(application);
        return application.Id!.Value;
    }

    private Migration CreateMigration(
        bool enabled = true,
        bool apply = false,
        string? confirmedBy = "A Reviewer",
        bool registerPersistence = true,
        bool dualWrite = false
    )
    {
        var services = new ServiceCollection();
        if (registerPersistence)
            services.AddSingleton<IAccreditationApplicationPersistence>(_persistence);
        services.AddSingleton(Options.Create(new DomainModelConfig { DualWrite = dualWrite }));
        return new Migration(
            services.BuildServiceProvider(),
            Options.Create(
                new LastYearsAccreditationBackfillOptions
                {
                    Enabled = enabled,
                    Apply = apply,
                    SpotCheckConfirmedBy = confirmedBy,
                }
            ),
            _logger
        );
    }

    private Task<Migration.RunResult> ApplyAsync() =>
        CreateMigration(apply: true).RunAsync(Ct);

    private async Task<List<BsonDocument>> SnapshotAsync() =>
        await _raw.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(Ct);

    private async Task<AccreditationApplicationModel> ReadAsync(ObjectId id) =>
        await _factory
            .GetCollection<AccreditationApplicationModel>("accreditationApplications")
            .Find(a => a.Id == id)
            .FirstAsync(Ct);

    private IEnumerable<string> Logged(LogLevel level) =>
        _logger.Entries.Where(e => e.LogLevel == level).Select(e => e.Message);

    private string LoggedFor(ObjectId id) =>
        _logger.Entries.Single(e => e.Message.Contains($"application {id} (") && e.Message.Contains(", issued number ")).Message;

    // --- gates ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Disabled_is_a_no_op_that_never_resolves_the_persistence()
    {
        await SeedAsync(Egger());
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: false, apply: true, registerPersistence: false)
            .RunAsync(Ct);

        result.Should().Be(Migration.RunResult.Disabled);
        (await SnapshotAsync()).Should().Equal(before);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_reports_every_change_and_writes_nothing()
    {
        await SeedAsync(Egger());
        await SeedAsync(Clearfield());
        var before = await SnapshotAsync();

        var result = await CreateMigration().RunAsync(Ct);

        result.Should().Be(Migration.RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Information)
            .Should()
            .Contain(m => m.Contains("DRY RUN") && m.Contains(EggerAccreditation))
            .And.Contain(m =>
                m.Contains("DRY RUN")
                && m.Contains($"issued {ClearfieldIncorrect} is corrected to {ClearfieldCorrected}")
            );
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains("APPLIED"));
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_without_a_spot_check_runs_as_a_dry_run()
    {
        await SeedAsync(Egger());
        var before = await SnapshotAsync();

        var result = await CreateMigration(apply: true, confirmedBy: " ").RunAsync(Ct);

        result.Should().Be(Migration.RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().ContainSingle(m => m.Contains("SpotCheckConfirmedBy"));
    }

    [Fact]
    public async Task Apply_with_dual_write_on_runs_as_a_dry_run()
    {
        await SeedAsync(Egger());
        var before = await SnapshotAsync();

        var result = await CreateMigration(apply: true, dualWrite: true).RunAsync(Ct);

        result.Should().Be(Migration.RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().ContainSingle(m => m.Contains("DualWrite"));
    }

    // --- last year's accreditation ----------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_gives_an_application_last_years_accreditation_from_the_register()
    {
        var id = await SeedAsync(Egger());

        var result = await ApplyAsync();

        result.Should().Be(Migration.RunResult.Applied);
        var application = await ReadAsync(id);
        application.LastYearsAccreditation!.Year.Should().Be(2026);
        application.LastYearsAccreditation.AccreditationNumber.Should().Be(EggerAccreditation);
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m =>
                m.Contains("APPLIED")
                && m.Contains(EggerAccreditation)
                && m.Contains("A Reviewer")
            );
    }

    [Fact]
    public async Task An_exporter_is_matched_on_its_registration()
    {
        var id = await SeedAsync(
            Application(GapRegistration, 500308, isExporter: true)
        );

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditation!.AccreditationNumber.Should().Be(GapAccreditation);
    }

    [Fact]
    public async Task A_regenerated_registration_number_is_matched_on_the_previous_one()
    {
        var application = Egger();
        application.RegistrationReference = "R27ER5000270999WO";
        application.PreviousRegistrationNumbers = [EggerRegistration];
        var id = await SeedAsync(application);

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditation!.AccreditationNumber.Should().Be(EggerAccreditation);
    }

    [Fact]
    public async Task A_draft_without_its_org_yet_is_matched_on_the_registration_alone()
    {
        var id = await SeedAsync(Application(EggerRegistration, null, MaterialType.Wood));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditation!.AccreditationNumber.Should().Be(EggerAccreditation);
    }

    [Fact]
    public async Task Only_2027_applications_are_touched()
    {
        var id = await SeedAsync(
            Application(EggerRegistration, 500027, MaterialType.Wood, year: 2026)
        );

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditation.Should().BeNull();
    }

    [Fact]
    public async Task A_registered_only_operator_gets_nothing()
    {
        var id = await SeedAsync(Application(DtsRegistration, 500237));
        var before = await SnapshotAsync();

        await ApplyAsync();

        (await SnapshotAsync()).Should().Equal(before);
        LoggedFor(id).Should().Contain(nameof(Migration.Outcome.RegisteredOnly));
    }

    [Fact]
    public async Task The_same_number_already_held_is_left_alone()
    {
        var application = Egger();
        application.LastYearsAccreditation = new() { Year = 2026, AccreditationNumber = EggerAccreditation };
        var id = await SeedAsync(application);
        var before = await SnapshotAsync();

        await ApplyAsync();

        (await SnapshotAsync()).Should().Equal(before);
        LoggedFor(id).Should().Contain(nameof(Migration.Outcome.AlreadyHeld));
    }

    [Fact]
    public async Task A_different_number_already_held_is_reported_and_never_overwritten()
    {
        var application = Egger();
        application.LastYearsAccreditation = new() { Year = 2026, AccreditationNumber = "A26ER5000270001WO" };
        var id = await SeedAsync(application);
        var before = await SnapshotAsync();

        await ApplyAsync();

        (await SnapshotAsync()).Should().Equal(before);
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.Outcome.HoldsADifferentNumber))
            );
    }

    [Theory]
    [InlineData(500028, MaterialType.Wood, false)]
    [InlineData(500027, MaterialType.Plastic, false)]
    [InlineData(500027, MaterialType.Wood, true)]
    public async Task A_register_row_that_is_not_the_applications_registration_is_skipped_and_reported(
        int orgId,
        MaterialType material,
        bool isExporter
    )
    {
        var id = await SeedAsync(Application(EggerRegistration, orgId, material, isExporter));
        var other = await SeedAsync(Application(GapRegistration, 500308, isExporter: true));

        var result = await ApplyAsync();

        result.Should().Be(Migration.RunResult.Applied);
        (await ReadAsync(id)).LastYearsAccreditation.Should().BeNull();
        (await ReadAsync(other)).LastYearsAccreditation.Should().NotBeNull();
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.Outcome.IdentityMismatch))
            );
    }

    [Fact]
    public async Task A_held_back_register_row_is_not_used()
    {
        var id = await SeedAsync(Application(JpRecyclingRegistration, 500302));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditation.Should().BeNull();
        LoggedFor(id).Should().Contain(nameof(Migration.Outcome.HeldBack));
    }

    [Fact]
    public async Task An_application_not_on_the_register_is_reported()
    {
        var id = await SeedAsync(Application("R26ER5009990001PL", 500999));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditation.Should().BeNull();
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.Outcome.NotOnTheRegister))
            );
    }

    [Fact]
    public async Task A_second_apply_changes_nothing()
    {
        await SeedAsync(Egger());
        await SeedAsync(Clearfield());
        await ApplyAsync();
        var after = await SnapshotAsync();
        _logger.Entries.Clear();

        await ApplyAsync();

        (await SnapshotAsync()).Should().Equal(after);
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains("APPLIED"));
    }

    // --- issued numbers ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Apply_corrects_Clearfields_issued_number_and_keeps_the_old_one()
    {
        var id = await SeedAsync(Clearfield());

        await ApplyAsync();

        var application = await ReadAsync(id);
        application.AccreditationReference.Should().Be(ClearfieldCorrected);
        application.PreviousAccreditationNumbers.Should().Equal(ClearfieldIncorrect);
        application.LastYearsAccreditation!.AccreditationNumber.Should().Be(ClearfieldAccreditation);
        application.ApplicationStatus.Should().Be(ApplicationStatus.Approved);
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m =>
                m.Contains("APPLIED")
                && m.Contains($"{ClearfieldIncorrect} -> {ClearfieldCorrected}")
                && m.Contains("A Reviewer")
            );
    }

    [Fact]
    public async Task Clearfield_already_corrected_is_skipped()
    {
        var id = await SeedAsync(Clearfield(ClearfieldCorrected));

        await ApplyAsync();

        var application = await ReadAsync(id);
        application.AccreditationReference.Should().Be(ClearfieldCorrected);
        application.PreviousAccreditationNumbers.Should().BeEmpty();
        LoggedFor(id).Should().Contain(nameof(Migration.IssuedNumber.AlreadyCorrected));
        Logged(LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task Clearfields_correction_is_blocked_when_another_application_holds_the_corrected_number()
    {
        var id = await SeedAsync(Clearfield());
        await SeedAsync(Application("R26ER5009990001PL", 500999, accreditationReference: ClearfieldCorrected));

        await ApplyAsync();

        (await ReadAsync(id)).AccreditationReference.Should().Be(ClearfieldIncorrect);
        LoggedFor(id).Should().Contain(nameof(Migration.IssuedNumber.CorrectionBlocked));
    }

    [Fact]
    public async Task Another_wrongly_issued_number_is_reported_and_left_alone()
    {
        var application = Egger();
        application.AccreditationReference = "A27ER5000270036WO";
        var id = await SeedAsync(application);

        await ApplyAsync();

        (await ReadAsync(id)).AccreditationReference.Should().Be("A27ER5000270036WO");
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.IssuedNumber.WrongReportedOnly))
                && e.Message.Contains("A27ER5000270063WO")
            );
    }

    [Fact]
    public async Task A_correctly_issued_number_is_reported_as_correct()
    {
        var application = Egger();
        application.AccreditationReference = "A27ER5000270063WO";
        var id = await SeedAsync(application);

        await ApplyAsync();

        LoggedFor(id).Should().Contain(nameof(Migration.IssuedNumber.Correct));
    }

    [Fact]
    public async Task A_registered_only_operators_number_is_checked_against_its_registration()
    {
        var id = await SeedAsync(
            Application(DtsRegistration, 500237, accreditationReference: "A27ER5002370119PL")
        );

        await ApplyAsync();

        LoggedFor(id).Should().Contain(nameof(Migration.IssuedNumber.Correct));
    }

    [Fact]
    public async Task A_correction_that_matches_nothing_is_reported()
    {
        await SeedAsync(Egger());

        await CreateMigration().RunAsync(Ct);

        Logged(LogLevel.Warning)
            .Should()
            .ContainSingle(m => m.Contains("CLEARFIELD ENVIROTECH LIMITED") && m.Contains("nothing to correct"));
    }

    [Fact]
    public async Task The_summary_counts_every_outcome()
    {
        await SeedAsync(Egger());
        await SeedAsync(Application(DtsRegistration, 500237));
        await SeedAsync(Clearfield());

        await CreateMigration().RunAsync(Ct);

        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m =>
                m.Contains("complete")
                && m.Contains("applications=3")
                && m.Contains("Added=2")
                && m.Contains("RegisteredOnly=1")
                && m.Contains("Corrected=1")
            );
    }
}
