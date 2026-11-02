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

// RA-691: runs against the real ephemeral mongod so the guarded $set/$addToSet update and the
// reference lookups are exercised exactly as production runs them.
public sealed class AccreditationReferenceCorrectionMigrationTests : IDisposable
{
    private const string InfinityIncorrect = "A27ER5005270322PL";
    private const string InfinityCorrected = "A27ER5005270246PL";
    private const string PlasgranIncorrect = "A27ER5001050321PL";
    private const string PlasgranCorrected = "A27ER5001050197PL";
    private const string KronospanIncorrect = "A27WR5001080075WO";
    private const string KronospanCorrected = "A27WR5001080008WO";

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

    // The three incorrect records plus unrelated ones that must never change: another
    // accreditation of the same material for the same org (a second site) and a 2026 record, for
    // both a plastic and the wood target, and another org entirely.
    private async Task<(
        ObjectId Infinity,
        ObjectId Plasgran,
        ObjectId Kronospan
    )> SeedIncorrectStateAsync()
    {
        var infinity = await SeedAsync(Application(500527, InfinityIncorrect));
        var plasgran = await SeedAsync(Application(500105, PlasgranIncorrect));
        var kronospan = await SeedAsync(Kronospan(KronospanIncorrect));
        await SeedAsync(Application(500527, "A27ER5005270400PL"));
        await SeedAsync(Application(500527, "A26ER5005270246PL", year: 2026));
        await SeedAsync(Kronospan("A27WR5001080100WO"));
        await SeedAsync(Kronospan("A26WR5001080008WO", year: 2026));
        await SeedAsync(
            Application(500999, "A27EX5009990123GL", material: MaterialType.Glass, isExporter: true)
        );
        return (infinity.Id!.Value, plasgran.Id!.Value, kronospan.Id!.Value);
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

    [Fact]
    public async Task Dry_run_logs_exactly_the_three_changes_and_writes_nothing()
    {
        await SeedIncorrectStateAsync();
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: false)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        var changes = Logged(LogLevel.Information).Where(m => m.Contains(" -> ")).ToList();
        changes.Should().HaveCount(3);
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
        changes.Should().OnlyContain(m => m.Contains("DRY RUN"));
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
        var (infinityId, plasgranId, kronospanId) = await SeedIncorrectStateAsync();

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
        Logged(LogLevel.Information)
            .Where(m => m.StartsWith("RA-691 APPLIED"))
            .Should()
            .HaveCount(3)
            .And.OnlyContain(m => m.Contains("A Reviewer"));
    }

    [Fact]
    public async Task Apply_changes_no_other_record()
    {
        var (infinityId, plasgranId, kronospanId) = await SeedIncorrectStateAsync();
        var targets = new[] { infinityId, plasgranId, kronospanId };
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
        Logged(LogLevel.Information).Count(m => m.Contains("skipped")).Should().Be(3);
    }

    [Fact]
    public async Task Already_corrected_record_is_skipped_while_the_others_are_fixed()
    {
        var infinity = Application(500527, InfinityCorrected);
        infinity.PreviousAccreditationNumbers.Add(InfinityIncorrect);
        await SeedAsync(infinity);
        var plasgran = await SeedAsync(Application(500105, PlasgranIncorrect));
        var kronospan = await SeedAsync(Kronospan(KronospanIncorrect));
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
    }

    [Fact]
    public async Task Incorrect_value_already_in_previous_numbers_is_not_duplicated()
    {
        var infinity = Application(500527, InfinityIncorrect);
        infinity.PreviousAccreditationNumbers.Add(InfinityIncorrect);
        await SeedAsync(infinity);
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Kronospan(KronospanIncorrect));

        await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        (await ReadAsync(infinity.Id!.Value))
            .PreviousAccreditationNumbers.Should()
            .Equal(InfinityIncorrect);
    }

    [Fact]
    public async Task Record_holding_neither_value_aborts_the_whole_run_without_writes()
    {
        // PLASGRAN and Kronospan are fine, but Infinity's record holds some other value.
        await SeedAsync(Application(500527, "A27ER5005270999PL"));
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Kronospan(KronospanIncorrect));
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
        var before = await SnapshotAsync();

        var result = await CreateMigration(enabled: true, apply: true)
            .RunAsync(TestContext.Current.CancellationToken);

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
    }

    // The expected material is per correction: Kronospan's reference is a wood one, so a plastic
    // record holding it is not the target even though the two plastic corrections accept plastic.
    [Fact]
    public async Task Kronospan_reference_held_by_a_plastic_record_aborts_without_writes()
    {
        await SeedAsync(Application(500527, InfinityIncorrect));
        await SeedAsync(Application(500105, PlasgranIncorrect));
        await SeedAsync(Application(500108, KronospanIncorrect, material: MaterialType.Plastic));
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
