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
using RunResult = EprRegisterEnrolBackend.AccreditationApplication.Startup.AccreditationReferenceCorrectionMigration.RunResult;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-691 / RA-763: runs against the real ephemeral mongod so the guarded $set/$addToSet update and the
// reference lookups are exercised exactly as production runs them.
public sealed class AccreditationReferenceCorrectionMigrationTests : IDisposable
{
    private const string InfinityIncorrect = "A27ER5005270322PL";
    private const string InfinityCorrected = "A27ER5005270246PL";
    private const string PlasgranIncorrect = "A27ER5001050321PL";
    private const string PlasgranCorrected = "A27ER5001050197PL";
    private const string KronospanIncorrect = "A27WR5001080075WO";
    private const string KronospanCorrected = "A27WR5001080008WO";
    private const string ClearfieldIncorrect = "A27ER5003240079PL";
    private const string ClearfieldCorrected = "A27ER5003240164PL";

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _persistence;
    private readonly IMongoCollection<BsonDocument> _raw;
    private readonly CapturingLogger<AccreditationReferenceCorrectionMigration> _logger = new();

    public AccreditationReferenceCorrectionMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra691");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );
        _raw = _factory.GetCollection<BsonDocument>("accreditationApplications");
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static AccreditationApplicationModel Application(
        int orgId,
        string? reference,
        int year = 2027,
        MaterialType material = MaterialType.Plastic,
        bool isExporter = false
    ) =>
        new()
        {
            OrganisationId = $"reex-{orgId}",
            OrgId = orgId,
            Year = year,
            MaterialType = material,
            IsExporter = isExporter,
            ApplicationStatus = ApplicationStatus.Approved,
            ApplicationReference = $"APP-{Guid.NewGuid():N}",
            RegistrationReference = $"R27ER{orgId}0001PL",
            AccreditationReference = reference,
        };

    private async Task<AccreditationApplicationModel> SeedAsync(AccreditationApplicationModel app)
    {
        await _persistence.CreateAsync(app);
        return app;
    }

    private static AccreditationApplicationModel Kronospan(string reference, int year = 2027) =>
        Application(500108, reference, year, MaterialType.Wood);

    private static AccreditationApplicationModel Clearfield(string reference, int year = 2027) =>
        Application(500324, reference, year);

    // The four incorrect records plus unrelated ones that must never change: another
    // accreditation of the same material for the same org (a second site) and a 2026 record, for
    // both a plastic and the wood target, Clearfield's 2026 accreditation (the reference its
    // correction keeps the sequence of), and another org entirely.
    private async Task<(
        ObjectId Infinity,
        ObjectId Plasgran,
        ObjectId Kronospan,
        ObjectId Clearfield
    )> SeedIncorrectStateAsync()
    {
        var infinity = await SeedAsync(Application(500527, InfinityIncorrect));
        var plasgran = await SeedAsync(Application(500105, PlasgranIncorrect));
        var kronospan = await SeedAsync(Kronospan(KronospanIncorrect));
        var clearfield = await SeedAsync(Clearfield(ClearfieldIncorrect));
        await SeedAsync(Application(500527, "A27ER5005270400PL"));
        await SeedAsync(Application(500527, "A26ER5005270246PL", year: 2026));
        await SeedAsync(Kronospan("A27WR5001080100WO"));
        await SeedAsync(Kronospan("A26WR5001080008WO", year: 2026));
        await SeedAsync(Clearfield("A26ER5003240164PL", year: 2026));
        await SeedAsync(
            Application(500999, "A27EX5009990123GL", material: MaterialType.Glass, isExporter: true)
        );
        return (
            infinity.Id!.Value,
            plasgran.Id!.Value,
            kronospan.Id!.Value,
            clearfield.Id!.Value
        );
    }

    private static AccreditationApplicationModel AlreadyCorrected(
        AccreditationApplicationModel application,
        string incorrect
    )
    {
        application.PreviousAccreditationNumbers.Add(incorrect);
        return application;
    }

    // Prod as RA-763 finds it: the three RA-691 records were corrected on 2026-11-02 and
    // Clearfield still holds the reference issued from its registration.
    private async Task<(
        IReadOnlyList<ObjectId> Ra691,
        ObjectId Clearfield
    )> SeedRa691CorrectedStateAsync()
    {
        var infinity = await SeedAsync(
            AlreadyCorrected(Application(500527, InfinityCorrected), InfinityIncorrect)
        );
        var plasgran = await SeedAsync(
            AlreadyCorrected(Application(500105, PlasgranCorrected), PlasgranIncorrect)
        );
        var kronospan = await SeedAsync(
            AlreadyCorrected(Kronospan(KronospanCorrected), KronospanIncorrect)
        );
        var clearfield = await SeedAsync(Clearfield(ClearfieldIncorrect));
        return (
            [infinity.Id!.Value, plasgran.Id!.Value, kronospan.Id!.Value],
            clearfield.Id!.Value
        );
    }

    private AccreditationReferenceCorrectionMigration CreateMigration(
        bool enabled,
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
        return new AccreditationReferenceCorrectionMigration(
            services.BuildServiceProvider(),
            Options.Create(
                new AccreditationReferenceCorrectionOptions
                {
                    Enabled = enabled,
                    Apply = apply,
                    SpotCheckConfirmedBy = confirmedBy,
                }
            ),
            _logger
        );
    }

    private async Task<List<BsonDocument>> SnapshotAsync() =>
        await _raw.Find(FilterDefinition<BsonDocument>.Empty)
            .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
            .ToListAsync(TestContext.Current.CancellationToken);

    private async Task<AccreditationApplicationModel> ReadAsync(ObjectId id) =>
        await _factory
            .GetCollection<AccreditationApplicationModel>("accreditationApplications")
            .Find(a => a.Id == id)
            .FirstAsync(TestContext.Current.CancellationToken);

    private IEnumerable<string> Logged(LogLevel level) =>
        _logger.Entries.Where(e => e.LogLevel == level).Select(e => e.Message);

    [Fact]
    public async Task Disabled_is_a_no_op_that_never_resolves_the_persistence()
    {
        await SeedIncorrectStateAsync();
        var before = await SnapshotAsync();

        // No persistence registered: resolving it would throw.
        var result = await CreateMigration(enabled: false, apply: true, registerPersistence: false)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Disabled);
        (await SnapshotAsync()).Should().Equal(before);
        _logger.Entries.Should().BeEmpty();
    }

    // Run as the host runs it: a failure is logged rather than escaping ExecuteAsync and
    // stopping the host. Enabled with no persistence registered makes resolving it throw.
    [Fact]
    public async Task Failure_during_the_hosted_run_is_logged_and_does_not_escape()
    {
        var migration = CreateMigration(enabled: true, registerPersistence: false);

        await migration.StartAsync(TestContext.Current.CancellationToken);
        await migration.ExecuteTask!;

        migration.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
        _logger
            .Entries.Should()
            .Contain(e =>
                e.LogLevel == LogLevel.Error
                && e.Message == "RA-691/RA-763 accreditation reference correction failed"
                && e.Exception is InvalidOperationException
            );
    }

    [Fact]
    public async Task Dry_run_logs_exactly_the_four_changes_and_writes_nothing()
    {
        await SeedIncorrectStateAsync();
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: false)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        var changes = Logged(LogLevel.Information).Where(m => m.Contains(" -> ")).ToList();
        changes.Should().HaveCount(4);
        changes
            .Should()
            .ContainSingle(m =>
                m.Contains("Infinity Plastic Solutions Ltd (500527)")
                && m.Contains($"{InfinityIncorrect} -> {InfinityCorrected}")
            );
        changes
            .Should()
            .ContainSingle(m =>
                m.Contains("PLASGRAN LIMITED (500105)")
                && m.Contains($"{PlasgranIncorrect} -> {PlasgranCorrected}")
            );
        changes
            .Should()
            .ContainSingle(m =>
                m.Contains("Kronospan Limited (500108)")
                && m.Contains($"{KronospanIncorrect} -> {KronospanCorrected}")
            );
        changes
            .Should()
            .ContainSingle(m =>
                m.StartsWith("RA-763 DRY RUN")
                && m.Contains("CLEARFIELD ENVIROTECH LIMITED (500324)")
                && m.Contains($"{ClearfieldIncorrect} -> {ClearfieldCorrected}")
            );
        changes.Where(m => m.StartsWith("RA-691 DRY RUN")).Should().HaveCount(3);
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_without_a_spot_check_runs_as_a_dry_run_and_writes_nothing()
    {
        await SeedIncorrectStateAsync();
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true, confirmedBy: " ")
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().ContainSingle(m => m.Contains("SpotCheckConfirmedBy"));
    }

    // RA-644: the correction writes the legacy application directly, without the domain model, so with
    // dual-write on it would leave the model holding the incorrect numbers.
    [Fact]
    public async Task Apply_with_dual_write_on_runs_as_a_dry_run_and_writes_nothing()
    {
        await SeedIncorrectStateAsync();
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true, dualWrite: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().ContainSingle(m => m.Contains("DualWrite"));
    }

    [Fact]
    public async Task Apply_sets_the_corrected_references_and_keeps_the_incorrect_ones_as_previous()
    {
        var (infinityId, plasgranId, kronospanId, clearfieldId) = await SeedIncorrectStateAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Applied);
        var infinity = await ReadAsync(infinityId);
        infinity.AccreditationReference.Should().Be(InfinityCorrected);
        infinity.PreviousAccreditationNumbers.Should().Equal(InfinityIncorrect);
        var plasgran = await ReadAsync(plasgranId);
        plasgran.AccreditationReference.Should().Be(PlasgranCorrected);
        plasgran.PreviousAccreditationNumbers.Should().Equal(PlasgranIncorrect);
        var kronospan = await ReadAsync(kronospanId);
        kronospan.AccreditationReference.Should().Be(KronospanCorrected);
        kronospan.PreviousAccreditationNumbers.Should().Equal(KronospanIncorrect);
        var clearfield = await ReadAsync(clearfieldId);
        clearfield.AccreditationReference.Should().Be(ClearfieldCorrected);
        clearfield.PreviousAccreditationNumbers.Should().Equal(ClearfieldIncorrect);
        var applied = Logged(LogLevel.Information).Where(m => m.Contains(" APPLIED: ")).ToList();
        applied.Should().HaveCount(4).And.OnlyContain(m => m.Contains("A Reviewer"));
        applied.Count(m => m.StartsWith("RA-691 APPLIED")).Should().Be(3);
        applied
            .Should()
            .ContainSingle(m =>
                m.StartsWith("RA-763 APPLIED")
                && m.Contains("CLEARFIELD ENVIROTECH LIMITED (500324)")
                && m.Contains($"{ClearfieldIncorrect} -> {ClearfieldCorrected}")
            );
    }

    [Fact]
    public async Task Apply_changes_no_other_record()
    {
        var (infinityId, plasgranId, kronospanId, clearfieldId) = await SeedIncorrectStateAsync();
        var targets = new[] { infinityId, plasgranId, kronospanId, clearfieldId };
        var before = (await SnapshotAsync())
            .Where(d => !targets.Contains(d["_id"].AsObjectId))
            .ToList();

        await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        (await SnapshotAsync())
            .Where(d => !targets.Contains(d["_id"].AsObjectId))
            .Should()
            .Equal(before);
    }

    [Fact]
    public async Task Second_apply_changes_nothing()
    {
        await SeedIncorrectStateAsync();
        await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);
        var afterFirst = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Applied);
        (await SnapshotAsync()).Should().Equal(afterFirst);
        Logged(LogLevel.Information).Count(m => m.Contains("skipped")).Should().Be(4);
    }

    [Fact]
    public async Task Already_corrected_record_is_skipped_while_the_others_are_fixed()
    {
        var infinity = Application(500527, InfinityCorrected);
        infinity.PreviousAccreditationNumbers.Add(InfinityIncorrect);
        await SeedAsync(infinity);
        var plasgran = await SeedAsync(Application(500105, PlasgranIncorrect));
        var kronospan = await SeedAsync(Kronospan(KronospanIncorrect));
        var clearfield = await SeedAsync(Clearfield(ClearfieldIncorrect));
        var infinityBefore = await _raw.Find(new BsonDocument("_id", infinity.Id!.Value))
            .FirstAsync(TestContext.Current.CancellationToken);

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Applied);
        (
            await _raw.Find(new BsonDocument("_id", infinity.Id!.Value))
                .FirstAsync(TestContext.Current.CancellationToken)
        )
            .Should()
            .Equal(infinityBefore);
        (await ReadAsync(plasgran.Id!.Value)).AccreditationReference.Should().Be(PlasgranCorrected);
        (await ReadAsync(kronospan.Id!.Value))
            .AccreditationReference.Should()
            .Be(KronospanCorrected);
        (await ReadAsync(clearfield.Id!.Value))
            .AccreditationReference.Should()
            .Be(ClearfieldCorrected);
    }

    [Fact]
    public async Task Dry_run_after_RA_691_reports_the_three_as_skipped_and_only_Clearfield_as_a_change()
    {
        await SeedRa691CorrectedStateAsync();
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: false)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        var info = Logged(LogLevel.Information).ToList();
        info.Where(m => m.Contains("skipped"))
            .Should()
            .HaveCount(3)
            .And.OnlyContain(m => m.StartsWith("RA-691 DRY RUN"));
        info.Where(m => m.Contains(" -> "))
            .Should()
            .ContainSingle()
            .Which.Should()
            .StartWith("RA-763 DRY RUN")
            .And.Contain("CLEARFIELD ENVIROTECH LIMITED (500324)")
            .And.Contain($"{ClearfieldIncorrect} -> {ClearfieldCorrected}");
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_after_RA_691_leaves_the_three_untouched_and_corrects_Clearfield()
    {
        var (ra691, clearfieldId) = await SeedRa691CorrectedStateAsync();
        var ra691Before = (await SnapshotAsync())
            .Where(d => ra691.Contains(d["_id"].AsObjectId))
            .ToList();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Applied);
        (await SnapshotAsync())
            .Where(d => ra691.Contains(d["_id"].AsObjectId))
            .Should()
            .Equal(ra691Before);
        var clearfield = await ReadAsync(clearfieldId);
        clearfield.AccreditationReference.Should().Be(ClearfieldCorrected);
        clearfield.PreviousAccreditationNumbers.Should().Equal(ClearfieldIncorrect);
        Logged(LogLevel.Information)
            .Where(m => m.Contains(" APPLIED: "))
            .Should()
            .ContainSingle()
            .Which.Should()
            .StartWith("RA-763 APPLIED")
            .And.Contain("A Reviewer");
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    // Clearfield is planned last, so a missing Clearfield record must still stop the three
    // earlier, valid fixes from being written.
    [Fact]
    public async Task Missing_Clearfield_record_aborts_the_whole_run_without_writes()
    {
        await SeedAsync(Application(500527, InfinityIncorrect));
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Kronospan(KronospanIncorrect));
        await SeedAsync(Clearfield("A27ER5003240999PL"));
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error)
            .Should()
            .Contain(m =>
                m.StartsWith("RA-763 CLEARFIELD ENVIROTECH LIMITED (500324)")
                && m.Contains("matches neither")
            );
    }

    [Theory]
    [InlineData(500999, 2027, MaterialType.Plastic, false)]
    [InlineData(500324, 2026, MaterialType.Plastic, false)]
    [InlineData(500324, 2027, MaterialType.Wood, false)]
    [InlineData(500324, 2027, MaterialType.Plastic, true)]
    public async Task Clearfield_reference_held_by_a_record_that_is_not_its_accreditation_aborts_without_writes(
        int orgId,
        int year,
        MaterialType material,
        bool isExporter
    )
    {
        await SeedAsync(Application(500527, InfinityIncorrect));
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Kronospan(KronospanIncorrect));
        await SeedAsync(Application(orgId, ClearfieldIncorrect, year, material, isExporter));
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error)
            .Should()
            .Contain(m =>
                m.StartsWith("RA-763 CLEARFIELD ENVIROTECH LIMITED (500324)")
                && m.Contains("expected OrgId 500324, Year 2027, Plastic, reprocessor")
            );
    }

    [Fact]
    public async Task Incorrect_value_already_in_previous_numbers_is_not_duplicated()
    {
        var infinity = Application(500527, InfinityIncorrect);
        infinity.PreviousAccreditationNumbers.Add(InfinityIncorrect);
        await SeedAsync(infinity);
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Kronospan(KronospanIncorrect));
        await SeedAsync(Clearfield(ClearfieldIncorrect));

        await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        (await ReadAsync(infinity.Id!.Value))
            .PreviousAccreditationNumbers.Should()
            .Equal(InfinityIncorrect);
    }

    [Fact]
    public async Task Record_holding_neither_value_aborts_the_whole_run_without_writes()
    {
        // PLASGRAN, Kronospan and Clearfield are fine, but Infinity's record holds some other value.
        await SeedAsync(Application(500527, "A27ER5005270999PL"));
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Kronospan(KronospanIncorrect));
        await SeedAsync(Clearfield(ClearfieldIncorrect));
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().Contain(m => m.Contains("matches neither"));
    }

    [Fact]
    public async Task Corrected_value_held_by_another_record_aborts_the_whole_run_without_writes()
    {
        await SeedIncorrectStateAsync();
        await SeedAsync(Application(500105, PlasgranCorrected));
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().Contain(m => m.Contains("is already held by application"));
    }

    [Fact]
    public async Task Incorrect_value_held_by_more_than_one_record_aborts_without_writes()
    {
        await SeedIncorrectStateAsync();
        await SeedAsync(Application(500527, InfinityIncorrect));
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
    }

    [Theory]
    [InlineData(500999, 2027, MaterialType.Plastic, false)]
    [InlineData(500527, 2026, MaterialType.Plastic, false)]
    [InlineData(500527, 2027, MaterialType.Glass, false)]
    [InlineData(500527, 2027, MaterialType.Plastic, true)]
    public async Task Value_held_by_a_record_that_is_not_the_target_accreditation_aborts_without_writes(
        int orgId,
        int year,
        MaterialType material,
        bool isExporter
    )
    {
        await SeedAsync(Application(orgId, InfinityIncorrect, year, material, isExporter));
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Kronospan(KronospanIncorrect));
        await SeedAsync(Clearfield(ClearfieldIncorrect));
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
    }

    // The expected material is per correction: Kronospan's reference is a wood one, so a plastic
    // record holding it is not the target even though the three plastic corrections accept plastic.
    [Fact]
    public async Task Kronospan_reference_held_by_a_plastic_record_aborts_without_writes()
    {
        await SeedAsync(Application(500527, InfinityIncorrect));
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Application(500108, KronospanIncorrect, material: MaterialType.Plastic));
        await SeedAsync(Clearfield(ClearfieldIncorrect));
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error)
            .Should()
            .Contain(m =>
                m.Contains("Kronospan Limited (500108)")
                && m.Contains("Plastic")
                && m.Contains("expected OrgId 500108, Year 2027, Wood, reprocessor")
            );
    }
}
