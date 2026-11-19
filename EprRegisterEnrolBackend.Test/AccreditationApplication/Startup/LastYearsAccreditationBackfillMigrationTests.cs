using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;
using MongoDB.Bson;
using MongoDB.Driver;
using Migration = EprRegisterEnrolBackend.AccreditationApplication.Startup.LastYearsAccreditationBackfillMigration;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-784: runs against the real ephemeral mongod, with rows from the real public register, so the
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

    // Clearfield Envirotech: issued a number derived from its registration (corrected by RA-785, not here).
    private const string ClearfieldRegistration = "R26ER5003240079PL";
    private const string ClearfieldAccreditation = "A26ER5003240164PL";
    private const string ClearfieldIssued = "A27ER5003240079PL";

    // Published rows that look wrong but are used as published.
    private const string JpRecyclingRegistration = "R26WR5003020027PL";
    private const string JpRecyclingAccreditation = "A26WR5005320025PL";
    private const string ComhairleRegistration = "R26SR5005410022GO";
    private const string ComhairleAccreditation = "A26SR5005410022GL";
    private const string EncircRegistration = "R26ER5002260055GR";
    private const string EncircAccreditation = "A26ER5002260023GR";
    private const string JAndAYoungRegistration = "R26WR5000950025PL";

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

    private static AccreditationApplicationModel Clearfield(string? issued = ClearfieldIssued) =>
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
        IDualWriteUnitOfWork? unitOfWork = null
    )
    {
        var services = new ServiceCollection();
        if (registerPersistence)
            services.AddSingleton<IAccreditationApplicationPersistence>(_persistence);
        services.AddSingleton(unitOfWork ?? new LegacyOnlyUnitOfWork());
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
            .Contain(m => m.Contains("DRY RUN") && m.Contains(EggerAccreditation));
        // Clearfield's issued number doesn't fit the register, so its line is a warning.
        Logged(LogLevel.Warning)
            .Should()
            .Contain(m => m.Contains("DRY RUN") && m.Contains(ClearfieldAccreditation));
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

    // With DomainModel:DualWrite on, the write goes through the unit of work: the application is migrated if it isn't
    // already, and the domain model's Application and Registration take last year's number in the same transaction.
    [Fact]
    public async Task Apply_with_dual_write_on_writes_the_domain_model_too()
    {
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        var stores = new ApplicationGroupStores(
            new OrganisationPersistence(_factory, logs),
            new RegistrationPersistence(_factory, logs, clock),
            new OverseasSitePersistence(_factory, logs, clock),
            new ApplicationVersionPersistence(_factory, logs, clock),
            new AccreditationPersistence(_factory, logs, clock),
            new MigrationConflictPersistence(_factory, logs)
        );
        var meters = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider()
            .GetRequiredService<IMeterFactory>();
        var unitOfWork = new DualWriteUnitOfWork(
            _factory,
            new ApplicationGroupMigrator(_persistence, stores, clock),
            new ApplicationModelSync(_persistence, stores, clock, NullLogger<ApplicationModelSync>.Instance),
            new DualWriteMetrics(meters),
            NullLogger<DualWriteUnitOfWork>.Instance
        );
        var egger = Egger();
        egger.OrganisationId = "6512a0f0c0ffee0000000001";
        egger.RegistrationId = "6512a0f0c0ffee0000000002";
        egger.Nation = Nation.England;
        var id = await SeedAsync(egger);

        var result = await CreateMigration(apply: true, unitOfWork: unitOfWork).RunAsync(Ct);

        result.Should().Be(Migration.RunResult.Applied);
        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(EggerAccreditation);
        var application = await _factory
            .GetCollection<ApplicationEntity>(DomainModelCollections.ApplicationVersions)
            .Find(a => a.IsCurrent && a.EntityId == id)
            .SingleAsync(Ct);
        application.AsAccreditation().LastYearsAccreditationNumber!.AccreditationNumber
            .Should()
            .Be(EggerAccreditation);
        var registration = await _factory
            .GetCollection<RegistrationEntity>(DomainModelCollections.RegistrationVersions)
            .Find(r => r.IsCurrent && r.EntityId == application.RegistrationId!.Value)
            .SingleAsync(Ct);
        registration.AccreditationNumbers.Should()
            .ContainSingle(n => n.Year == 2026 && n.AccreditationNumber == EggerAccreditation);
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    // --- last year's accreditation ----------------------------------------------------------------------------------

    // Whoever reviews the dry run sees which organisation each line is about, and what would be written.
    [Fact]
    public async Task Dry_run_and_applied_lines_name_the_organisation()
    {
        var egger = Egger();
        egger.OrganisationName = "Egger (UK) Limited";
        var id = await SeedAsync(egger);

        await ApplyAsync();

        var plan = LoggedFor(id);
        plan.Should().Contain("org 500027 Egger (UK) Limited").And.Contain(EggerAccreditation);
        Logged(LogLevel.Information)
            .Should()
            .Contain(m =>
                m.StartsWith("RA-784 APPLIED") && m.Contains("org 500027 Egger (UK) Limited")
            );
    }

    [Fact]
    public async Task Apply_gives_an_application_last_years_accreditation_from_the_register()
    {
        var id = await SeedAsync(Egger());

        var result = await ApplyAsync();

        result.Should().Be(Migration.RunResult.Applied);
        var application = await ReadAsync(id);
        application.LastYearsAccreditationNumber!.Year.Should().Be(2026);
        application.LastYearsAccreditationNumber.AccreditationNumber.Should().Be(EggerAccreditation);
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

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(GapAccreditation);
    }

    [Fact]
    public async Task A_regenerated_registration_number_is_matched_on_the_previous_one()
    {
        var application = Egger();
        application.RegistrationReference = "R27ER5000270999WO";
        application.PreviousRegistrationNumbers = [EggerRegistration];
        var id = await SeedAsync(application);

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(EggerAccreditation);
    }

    [Fact]
    public async Task A_draft_without_its_org_yet_is_matched_on_the_registration_alone()
    {
        var id = await SeedAsync(Application(EggerRegistration, null, MaterialType.Wood));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(EggerAccreditation);
    }

    [Fact]
    public async Task Only_2027_applications_are_touched()
    {
        var id = await SeedAsync(
            Application(EggerRegistration, 500027, MaterialType.Wood, year: 2026)
        );

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber.Should().BeNull();
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
        application.LastYearsAccreditationNumber = new() { Year = 2026, AccreditationNumber = EggerAccreditation };
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
        application.LastYearsAccreditationNumber = new() { Year = 2026, AccreditationNumber = "A26ER5000270001WO" };
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
        (await ReadAsync(id)).LastYearsAccreditationNumber.Should().BeNull();
        (await ReadAsync(other)).LastYearsAccreditationNumber.Should().NotBeNull();
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.Outcome.IdentityMismatch))
            );
    }

    // Agreed 19 November 2026: the register is used as published, even where a row looks wrong.
    [Fact]
    public async Task A_row_whose_accreditation_names_another_org_is_used_as_published()
    {
        var id = await SeedAsync(Application(JpRecyclingRegistration, 500302));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber
            .Should()
            .Be(JpRecyclingAccreditation);
    }

    [Fact]
    public async Task A_glass_number_ending_GL_is_used_as_published()
    {
        var application = Application(ComhairleRegistration, 500541, MaterialType.Glass);
        application.GlassRecyclingProcess = GlassRecyclingProcess.Other;
        var id = await SeedAsync(application);

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber
            .Should()
            .Be(ComhairleAccreditation);
    }

    // Encirc: org 500069 holds a registration carrying 500226 (RA-764), as the register's Org ID column says.
    [Theory]
    [InlineData(500069)]
    [InlineData(500226)]
    public async Task The_org_is_matched_on_the_registration_number_or_the_registers_Org_ID(int orgId)
    {
        var id = await SeedAsync(Application(EncircRegistration, orgId, MaterialType.Glass));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber
            .Should()
            .Be(EncircAccreditation);
    }

    // J. and A. Young: the register prints the number without its last letter. An application still holding that
    // can't be issued a 2027 number, so it is reported, not corrected.
    [Fact]
    public async Task J_and_A_Youngs_registration_number_missing_its_last_letter_is_reported()
    {
        var id = await SeedAsync(
            Application(PublicRegister2026.JAndAYoungPublishedRegistrationNumber, 500095)
        );
        var before = await SnapshotAsync();

        await ApplyAsync();

        (await SnapshotAsync()).Should().Equal(before);
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.Outcome.RegistrationNumberIncomplete))
                && e.Message.Contains("must end PL")
            );
    }

    [Fact]
    public async Task J_and_A_Young_with_the_full_registration_number_is_registered_only()
    {
        var id = await SeedAsync(Application(JAndAYoungRegistration, 500095));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber.Should().BeNull();
        LoggedFor(id).Should().Contain(nameof(Migration.Outcome.RegisteredOnly));
    }

    [Fact]
    public async Task An_application_not_on_the_register_is_reported()
    {
        var id = await SeedAsync(Application("R26ER5009990001PL", 500999));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber.Should().BeNull();
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

    // RA-785 corrects wrongly issued numbers; this only reports them.
    [Fact]
    public async Task A_wrongly_issued_renewal_number_is_reported_and_left_alone()
    {
        var id = await SeedAsync(Clearfield());

        await ApplyAsync();

        var application = await ReadAsync(id);
        application.AccreditationReference.Should().Be(ClearfieldIssued);
        application.PreviousAccreditationNumbers.Should().BeEmpty();
        application.LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(ClearfieldAccreditation);
        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.IssuedNumber.DoesNotFitTheRegister))
                && e.Message.Contains(ClearfieldAccreditation)
            );
    }

    [Fact]
    public async Task A_renewal_number_keeping_last_years_sequence_fits_the_register()
    {
        var application = Egger();
        application.AccreditationReference = "A27ER5000270063WO";
        var id = await SeedAsync(application);

        await ApplyAsync();

        LoggedFor(id).Should().Contain(nameof(Migration.IssuedNumber.FitsTheRegister));
    }

    // RA-783: the material code is the application's, so a glass renewal of a GL number ends GO or GR.
    [Fact]
    public async Task A_glass_renewal_of_a_GL_number_fits_the_register()
    {
        var application = Application(
            ComhairleRegistration,
            500541,
            MaterialType.Glass,
            accreditationReference: "A27SR5005410022GO",
            status: ApplicationStatus.Approved
        );
        application.GlassRecyclingProcess = GlassRecyclingProcess.Other;
        var id = await SeedAsync(application);

        await ApplyAsync();

        LoggedFor(id).Should().Contain(nameof(Migration.IssuedNumber.FitsTheRegister));
    }

    // RA-783: a first-time number keeps the registration's agency, process type and org, with a random sequence.
    [Theory]
    [InlineData("A27ER5002370042PL", nameof(Migration.IssuedNumber.FitsTheRegister))]
    [InlineData("A27ER5002380042PL", nameof(Migration.IssuedNumber.DoesNotFitTheRegister))]
    public async Task A_registered_only_operators_number_is_checked_against_its_registration(
        string issued,
        string expected
    )
    {
        var id = await SeedAsync(
            Application(DtsRegistration, 500237, accreditationReference: issued)
        );

        await ApplyAsync();

        LoggedFor(id).Should().Contain(expected);
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
                && m.Contains("DoesNotFitTheRegister=1")
            );
    }
}
