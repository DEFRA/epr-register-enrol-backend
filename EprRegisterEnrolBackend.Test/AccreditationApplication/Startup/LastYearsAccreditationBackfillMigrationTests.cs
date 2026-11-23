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
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics.Metrics;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;
using Migration = EprRegisterEnrolBackend.AccreditationApplication.Startup.LastYearsAccreditationBackfillMigration;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-784: runs against the real ephemeral mongod, with rows from the real public register, so the
// guarded updates and the matching run against real Mongo. Most tests write through the legacy-only
// unit of work; Apply_with_dual_write_on_writes_the_domain_model_too uses the real dual-write one, as
// prod does.
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
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra784");
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

        await CreateMigration().RunAsync(Ct);
        var dryRun = LoggedFor(id);
        _logger.Entries.Clear();
        await ApplyAsync();

        dryRun.Should()
            .Contain("DRY RUN")
            .And.Contain("org 500027 Egger (UK) Limited")
            .And.Contain(EggerAccreditation);
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

    // A registered-only row can't confirm a number the application already holds, so a person checks it. The issued
    // number is checked as a renewal of the held one, because that's what the issuer would renew from.
    [Fact]
    public async Task A_number_already_held_against_a_registered_only_row_is_reported_and_left_alone()
    {
        var application = Application(DtsRegistration, 500237, accreditationReference: "A27ER5002370119PL");
        application.LastYearsAccreditationNumber = new() { Year = 2026, AccreditationNumber = "A26ER5002370042PL" };
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
                && e.Message.Contains($"register row {DtsRegistration} has no accreditation")
                && e.Message.Contains(nameof(Migration.IssuedNumber.DoesNotFitTheRegister))
                && e.Message.Contains("not a renewal of A26ER5002370042PL")
            );
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains("APPLIED"));
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

    // The registration number is the only key: the org, material and process type aren't compared.
    [Theory]
    [InlineData(500028, MaterialType.Wood, false)]
    [InlineData(500027, MaterialType.Plastic, false)]
    [InlineData(500027, MaterialType.Wood, true)]
    public async Task An_application_is_matched_on_its_registration_number_alone(
        int orgId,
        MaterialType material,
        bool isExporter
    )
    {
        var id = await SeedAsync(Application(EggerRegistration, orgId, material, isExporter));

        await ApplyAsync();

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(EggerAccreditation);
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

    // Encirc: org 500069 holds a registration carrying 500226 (RA-764).
    [Fact]
    public async Task An_org_holding_a_registration_carrying_another_orgs_digits_is_matched()
    {
        var id = await SeedAsync(Application(EncircRegistration, 500069, MaterialType.Glass));

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

    // --- the end-of-run report --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_report_lists_each_application_not_on_the_register_with_its_org_and_registration()
    {
        var application = Application("R26ER5009990001PL", 500999);
        application.OrganisationName = "Not Registered Ltd";
        var id = await SeedAsync(application);
        await SeedAsync(Egger());

        await CreateMigration().RunAsync(Ct);

        Logged(LogLevel.Warning)
            .Should()
            .Contain(m => m.Contains("report") && m.Contains("1 2027 applications"))
            .And.ContainSingle(m =>
                m.Contains("not on the register: org 500999 Not Registered Ltd, registration R26ER5009990001PL")
                && m.Contains(id.ToString())
            );
    }

    [Fact]
    public async Task The_report_names_previous_registration_numbers_only_when_there_are_some()
    {
        var withoutPrevious = await SeedAsync(Application("R26ER5009990001PL", 500999));
        var regenerated = Application("R26ER5009980001PL", 500998);
        regenerated.PreviousRegistrationNumbers = ["R26ER5009980002PL"];
        var withPrevious = await SeedAsync(regenerated);

        await CreateMigration().RunAsync(Ct);

        var lines = Logged(LogLevel.Warning).Where(m => m.Contains("report: not on the register")).ToList();
        lines.Should().ContainSingle(m => m.Contains(withoutPrevious.ToString())).Which.Should().NotContain("previous");
        lines
            .Should()
            .ContainSingle(m => m.Contains(withPrevious.ToString()))
            .Which.Should()
            .Contain("registration R26ER5009980001PL (previous R26ER5009980002PL)");
    }

    [Fact]
    public async Task The_report_lists_every_register_row_no_application_used()
    {
        await SeedAsync(Egger());
        await SeedAsync(Application(DtsRegistration, 500237));

        await CreateMigration().RunAsync(Ct);

        var total = PublicRegister2026.ByRegistrationNumber.Count;
        var unusedLines = Logged(LogLevel.Information).Where(m => m.Contains("register row not used")).ToList();
        unusedLines.Should().HaveCount(total - 2);
        unusedLines.Should().NotContain(m => m.Contains(EggerRegistration) || m.Contains(DtsRegistration));
        unusedLines
            .Should()
            .Contain(m => m.Contains(ClearfieldRegistration) && m.Contains(ClearfieldAccreditation))
            .And.Contain(m => m.Contains(JAndAYoungRegistration) && m.Contains("none (registered only)"));
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m => m.Contains($"{total - 2} of {total} register rows matched no 2027 application"));
    }

    // Organisation by organisation, so the log reads one organisation at a time.
    [Fact]
    public async Task Applications_are_reported_organisation_by_organisation()
    {
        var gap = await SeedAsync(Application(GapRegistration, 500308, isExporter: true));
        var egger = await SeedAsync(Egger());

        await CreateMigration().RunAsync(Ct);

        var lines = _logger.Entries.Select(e => e.Message).ToList();
        lines.FindIndex(m => m.Contains(egger.ToString()))
            .Should()
            .BeLessThan(lines.FindIndex(m => m.Contains(gap.ToString())));
    }

    [Fact]
    public async Task An_issued_number_on_an_application_not_on_the_register_is_reported_as_not_checked()
    {
        var id = await SeedAsync(
            Application("R26ER5009990001PL", 500999, accreditationReference: "A27ER5009990001PL")
        );

        await CreateMigration().RunAsync(Ct);

        _logger
            .Entries.Should()
            .ContainSingle(e =>
                e.LogLevel == LogLevel.Warning
                && e.Message.Contains(id.ToString())
                && e.Message.Contains(nameof(Migration.IssuedNumber.NotChecked))
                && e.Message.Contains("could not be checked")
            );
    }

    // --- writes that don't happen ----------------------------------------------------------------------------------

    // Another instance (or anything else) gives the application a number between planning and writing: the guard
    // stops the write, and the run says so without overwriting it.
    [Fact]
    public async Task An_application_given_a_number_after_planning_is_not_overwritten()
    {
        var id = await SeedAsync(Egger());
        const string other = "A26ER5000270001WO";
        var racing = new BeforeTheWriteUnitOfWork(() =>
            _factory
                .GetCollection<AccreditationApplicationModel>("accreditationApplications")
                .UpdateOneAsync(
                    a => a.Id == id,
                    Builders<AccreditationApplicationModel>.Update.Set(
                        a => a.LastYearsAccreditationNumber,
                        new AccreditationNumberForYearModel { Year = 2026, AccreditationNumber = other }
                    ),
                    cancellationToken: Ct
                )
        );

        await CreateMigration(apply: true, unitOfWork: racing).RunAsync(Ct);

        (await ReadAsync(id)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(other);
        Logged(LogLevel.Error)
            .Should()
            .ContainSingle(m =>
                m.Contains(id.ToString()) && m.Contains("gained last year's accreditation after planning")
            );
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains("APPLIED"));
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m => m.Contains("planned: Added=1") && m.Contains("Written: Written=0, LostTheRace=1, Failed=0"));
    }

    // The unit of work rolls a failed write back and answers a problem: the run logs it and carries on.
    [Fact]
    public async Task A_write_that_fails_is_logged_and_the_run_carries_on()
    {
        var failed = await SeedAsync(Egger());
        var gap = await SeedAsync(Application(GapRegistration, 500308, isExporter: true));
        var failing = new FailingUnitOfWork(failed.ToString());

        var result = await CreateMigration(apply: true, unitOfWork: failing).RunAsync(Ct);

        result.Should().Be(Migration.RunResult.Applied);
        (await ReadAsync(failed)).LastYearsAccreditationNumber.Should().BeNull();
        (await ReadAsync(gap)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(GapAccreditation);
        Logged(LogLevel.Error)
            .Should()
            .ContainSingle(m => m.Contains(failed.ToString()) && m.Contains("write failed and was rolled back"));
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m => m.Contains("planned: Added=2") && m.Contains("Written: Written=1, LostTheRace=0, Failed=1"));
    }

    // Stopped part-way (here, the host shutting down after the first write): the reports are still logged, with the
    // writes done so far, and the cancellation carries on.
    [Fact]
    public async Task A_run_cancelled_part_way_still_logs_the_report()
    {
        var egger = await SeedAsync(Egger());
        var gap = await SeedAsync(Application(GapRegistration, 500308, isExporter: true));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var cancelling = new CancelAfterTheFirstWriteUnitOfWork(stopping);

        var run = () => CreateMigration(apply: true, unitOfWork: cancelling).RunAsync(stopping.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        (await ReadAsync(egger)).LastYearsAccreditationNumber!.AccreditationNumber.Should().Be(EggerAccreditation);
        (await ReadAsync(gap)).LastYearsAccreditationNumber.Should().BeNull();
        Logged(LogLevel.Warning).Should().Contain(m => m.Contains("report") && m.Contains("0 2027 applications"));
        Logged(LogLevel.Information).Should().Contain(m => m.Contains("register rows matched no 2027 application"));
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m =>
                m.Contains("stopped part-way")
                && m.Contains("planned: Added=2")
                && m.Contains("Written: Written=1, LostTheRace=0, Failed=0")
            );
    }

    private sealed class CancelAfterTheFirstWriteUnitOfWork(CancellationTokenSource stopping) : IDualWriteUnitOfWork
    {
        public async Task<IResult> RunAsync(
            string operation,
            string applicationId,
            Func<MongoDB.Driver.IClientSessionHandle?, CancellationToken, Task<IResult>> work,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await work(null, cancellationToken);
            await stopping.CancelAsync();
            return result;
        }
    }

    private sealed class BeforeTheWriteUnitOfWork(Func<Task> beforeTheWrite) : IDualWriteUnitOfWork
    {
        public async Task<IResult> RunAsync(
            string operation,
            string applicationId,
            Func<MongoDB.Driver.IClientSessionHandle?, CancellationToken, Task<IResult>> work,
            CancellationToken cancellationToken = default
        )
        {
            await beforeTheWrite();
            return await work(null, cancellationToken);
        }
    }

    private sealed class FailingUnitOfWork(string failingApplicationId) : IDualWriteUnitOfWork
    {
        public Task<IResult> RunAsync(
            string operation,
            string applicationId,
            Func<MongoDB.Driver.IClientSessionHandle?, CancellationToken, Task<IResult>> work,
            CancellationToken cancellationToken = default
        ) =>
            applicationId == failingApplicationId
                ? Task.FromResult(Results.Problem(statusCode: StatusCodes.Status500InternalServerError))
                : work(null, cancellationToken);
    }

    [Fact]
    public async Task The_summary_counts_the_outcomes_and_issued_numbers()
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
                && m.Contains("planned: Added=2")
                && m.Contains("RegisteredOnly=1")
                && m.Contains("Written: Written=0, LostTheRace=0, Failed=0")
                && m.Contains("DoesNotFitTheRegister=1")
            );
    }

    // Planned against actual: an APPLY run's summary says how many of the planned writes happened.
    [Fact]
    public async Task The_summary_says_how_many_planned_writes_were_written()
    {
        await SeedAsync(Egger());
        await SeedAsync(Application(GapRegistration, 500308, isExporter: true));

        await ApplyAsync();

        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m =>
                m.Contains("complete")
                && m.Contains("planned: Added=2")
                && m.Contains("Written: Written=2, LostTheRace=0, Failed=0")
            );
    }

    // --- when it runs ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_read_and_the_plan_are_logged()
    {
        await SeedAsync(Egger());
        await SeedAsync(Application(GapRegistration, 500308, isExporter: true));

        await CreateMigration().RunAsync(Ct);

        Logged(LogLevel.Information)
            .Should()
            .Contain(m => m.Contains("components ready"))
            .And.Contain(m => m.Contains("reading the 2027 applications, limit 00:02:00"))
            .And.Contain(m => m.Contains("read sent, waiting for the result"))
            .And.Contain(m => m.Contains("read finished in"))
            .And.Contain(m => m.Contains("read 2 2027 applications"))
            .And.Contain(m => m.Contains("planned 2 applications"));
    }

    // Started during start-up, the backfill stalled on ext-test; it now waits until the application has started.
    [Fact]
    public async Task The_backfill_reads_nothing_until_the_application_has_started()
    {
        using var started = new CancellationTokenSource();
        var reachedWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(_ =>
        {
            reachedWait.TrySetResult();
            return started.Token;
        });
        var persistence = EmptyPersistence();
        var migration = HostedMigration(persistence, lifetime);

        await migration.StartAsync(Ct);
        // The getter is only read by the wait, so without the wait this times out rather than passing vacuously.
        await reachedWait.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        migration.ExecuteTask!.IsCompleted.Should().BeFalse();
        await persistence.DidNotReceiveWithAnyArgs().GetByYearAsync(0, Ct);

        await started.CancelAsync();
        await migration.ExecuteTask.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await persistence.ReceivedWithAnyArgs(1).GetByYearAsync(0, Ct);
        Logged(LogLevel.Information).Should().Contain(m => m.Contains("backfill complete"));
    }

    [Fact]
    public async Task Shutting_down_before_the_application_has_started_reads_nothing()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Returns(CancellationToken.None);
        var persistence = EmptyPersistence();
        var migration = HostedMigration(persistence, lifetime);

        await migration.StartAsync(Ct);
        await migration.StopAsync(Ct);

        migration.ExecuteTask!.IsCompleted.Should().BeTrue();
        await persistence.DidNotReceiveWithAnyArgs().GetByYearAsync(0, Ct);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_read_that_takes_too_long_is_logged_as_a_failure_with_the_reason()
    {
        var slow = Substitute.For<IAccreditationApplicationPersistence>();
        slow.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Never(call.ArgAt<CancellationToken>(1)));

        await RunHostedAndExpectTimeoutAsync(slow);
    }

    // A read stuck somewhere that ignores the token (a connection handshake, say) still ends the wait.
    [Fact]
    public async Task A_read_that_ignores_the_token_is_still_cut_off()
    {
        var stuck = Substitute.For<IAccreditationApplicationPersistence>();
        stuck.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => new TaskCompletionSource<IReadOnlyList<AccreditationApplicationModel>>().Task);

        await RunHostedAndExpectTimeoutAsync(stuck);
    }

    // On ext-test the run logged "components ready" and then nothing, not even the timeout: a call that blocks before
    // handing back its task must still be cut off, and the log must show it never got as far as sending the read.
    [Fact]
    public async Task A_read_that_blocks_before_returning_is_still_cut_off_and_logged_as_not_sent()
    {
        using var release = new ManualResetEventSlim();
        var blocking = Substitute.For<IAccreditationApplicationPersistence>();
        blocking.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return Task.FromResult<IReadOnlyList<AccreditationApplicationModel>>([]);
            });

        try
        {
            await RunHostedAndExpectTimeoutAsync(blocking);
            Logged(LogLevel.Information).Should().NotContain(m => m.Contains("read sent"));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_read_still_waiting_is_logged_as_a_warning_until_it_ends()
    {
        var slow = Substitute.For<IAccreditationApplicationPersistence>();
        slow.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Never(call.ArgAt<CancellationToken>(1)));

        await RunHostedAndExpectTimeoutAsync(slow, progressInterval: TimeSpan.FromMilliseconds(200));

        _logger.Entries.Should()
            .Contain(e => e.LogLevel == LogLevel.Warning && e.Message.Contains("still reading the 2027 applications after"));
        Logged(LogLevel.Information).Should().Contain(m => m.Contains("read sent, waiting for the result"));
    }

    // The progress warning comes from our own interval; a TimeoutException from the read itself fails the run as it is.
    [Fact]
    public async Task A_timeout_from_the_read_itself_fails_the_run_unchanged()
    {
        var timedOut = Substitute.For<IAccreditationApplicationPersistence>();
        timedOut.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<AccreditationApplicationModel>>(new TimeoutException("selecting a server")));
        var migration = HostedMigration(timedOut, lifetime: null);

        var run = () => migration.RunAsync(Ct);

        await run.Should().ThrowAsync<TimeoutException>().WithMessage("selecting a server");
        _logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Warning);
    }

    // Only the limit we set is reported as a timeout; the caller's own cancellation stays a cancellation.
    [Fact]
    public async Task The_callers_cancellation_is_not_reported_as_a_timeout()
    {
        var slow = Substitute.For<IAccreditationApplicationPersistence>();
        slow.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => Never(call.ArgAt<CancellationToken>(1)));
        var migration = HostedMigration(slow, lifetime: null);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));

        var run = () => migration.RunAsync(caller.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    // A cancellation that is neither our limit nor the caller's (the driver's own, say) is not reported as a timeout.
    [Fact]
    public async Task A_cancellation_from_the_read_itself_is_not_reported_as_a_timeout()
    {
        var cancelled = Substitute.For<IAccreditationApplicationPersistence>();
        cancelled.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromCanceled<IReadOnlyList<AccreditationApplicationModel>>(new CancellationToken(canceled: true)));
        var migration = HostedMigration(cancelled, lifetime: null);

        var run = () => migration.RunAsync(Ct);

        (await run.Should().ThrowAsync<OperationCanceledException>()).Which.Should().NotBeOfType<TimeoutException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_read_limit_of_zero_or_less_fails_the_run_saying_how_to_set_it(int seconds)
    {
        var migration = HostedMigration(EmptyPersistence(), lifetime: null, readTimeout: TimeSpan.FromSeconds(seconds));

        var run = () => migration.RunAsync(Ct);

        await run.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ReadTimeout must be a positive duration as hh:mm:ss*");
    }

    private async Task RunHostedAndExpectTimeoutAsync(
        IAccreditationApplicationPersistence persistence,
        TimeSpan? progressInterval = null
    )
    {
        var migration = HostedMigration(persistence, lifetime: null, readTimeout: TimeSpan.FromSeconds(1), progressInterval);

        await migration.StartAsync(Ct);
        await migration.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var failure = _logger.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error && e.Message.Contains("backfill failed"))
            .Subject;
        failure.Exception.Should().BeOfType<TimeoutException>()
            .Which.Message.Should().Be("Reading the 2027 applications took longer than 00:00:01, so nothing was planned");
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains("planned"));
    }

    private static async Task<IReadOnlyList<AccreditationApplicationModel>> Never(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return [];
    }

    private static IAccreditationApplicationPersistence EmptyPersistence()
    {
        var persistence = Substitute.For<IAccreditationApplicationPersistence>();
        persistence.GetByYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AccreditationApplicationModel>>([]));
        return persistence;
    }

    // A hosted (ExecuteAsync) migration over the given persistence and lifetime, unlike CreateMigration's, which tests
    // call RunAsync on directly against the real Mongo persistence.
    private Migration HostedMigration(
        IAccreditationApplicationPersistence persistence,
        IHostApplicationLifetime? lifetime,
        TimeSpan? readTimeout = null,
        TimeSpan? progressInterval = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton(persistence);
        services.AddSingleton<IDualWriteUnitOfWork>(new LegacyOnlyUnitOfWork());
        if (lifetime is not null)
            services.AddSingleton(lifetime);
        return new Migration(
            services.BuildServiceProvider(),
            Options.Create(
                new LastYearsAccreditationBackfillOptions
                {
                    Enabled = true,
                    ReadTimeout = readTimeout ?? TimeSpan.FromMinutes(2),
                }
            ),
            _logger
        )
        {
            ReadProgressInterval = progressInterval ?? TimeSpan.FromSeconds(15),
        };
    }
}
