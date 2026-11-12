using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using RunResult = EprRegisterEnrolBackend.AccreditationApplication.Startup.OrganisationNameCorrectionMigration.RunResult;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

// RA-764: runs against the real ephemeral mongod so the guarded $set updates and the lookups are
// exercised exactly as production runs them.
public sealed class OrganisationNameCorrectionMigrationTests : IDisposable
{
    private const string OldName = "Encirc Ltd";
    private const string NewName = "VIDRALA UK & IRELAND LIMITED";
    private const string ReExId = "reex-500069";
    private const string Registration = "R26NR5000690003GL";
    private const string Accreditation2026 = "A26NR5000690003GL";
    private const string Accreditation2027 = "A27NR5000690003GL";
    private const string ApplicationsCollection = "accreditationApplications";

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _persistence;
    private readonly OrganisationPersistence _organisations;
    private readonly IMongoCollection<BsonDocument> _raw;
    private readonly IMongoCollection<BsonDocument> _rawOrganisations;
    private readonly CapturingLogger<OrganisationNameCorrectionMigration> _logger = new();

    public OrganisationNameCorrectionMigrationTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("ra764");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );
        _organisations = new OrganisationPersistence(_factory, NullLoggerFactory.Instance);
        _raw = _factory.GetCollection<BsonDocument>(ApplicationsCollection);
        _rawOrganisations = _factory.GetCollection<BsonDocument>(
            DomainModelCollections.Organisations
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static AccreditationApplicationModel Application(
        int? orgId = 500069,
        string? name = OldName,
        int year = 2027,
        string? registration = Registration,
        string? accreditation = Accreditation2027,
        string reExId = ReExId,
        ApplicationStatus status = ApplicationStatus.Approved
    ) =>
        new()
        {
            OrganisationId = reExId,
            OrgId = orgId,
            OrganisationName = name,
            Year = year,
            MaterialType = MaterialType.Glass,
            ApplicationStatus = status,
            ApplicationReference = $"APP-{Guid.NewGuid():N}",
            RegistrationReference = registration,
            AccreditationReference = accreditation,
            PreviousAccreditationNumbers = [Accreditation2026],
            PreviousRegistrationNumbers = ["R25NR5000690003GL"],
            DateLastEdited = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
            CaseManagementReference = "CASE-500069",
        };

    private async Task<ObjectId> SeedAsync(AccreditationApplicationModel app)
    {
        await _persistence.CreateAsync(app);
        return app.Id!.Value;
    }

    private async Task<ObjectId> SeedOrganisationAsync(
        string? name = OldName,
        string reExId = ReExId,
        int? number = 500069
    )
    {
        var organisation = new OrganisationEntity
        {
            Id = ObjectId.GenerateNewId(),
            ReExOrganisationId = reExId,
            OrganisationNumber = number,
            Name = name,
            TradingName = "Encirc",
            UpdatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
        };
        await _factory
            .GetCollection<OrganisationEntity>(DomainModelCollections.Organisations)
            .InsertOneAsync(organisation, cancellationToken: TestContext.Current.CancellationToken);
        return organisation.Id;
    }

    // The targets - the approved 2027 accreditation and a draft 2027 application not yet holding
    // its OrgId - plus records that must never change: 500069's 2026 application, another org's
    // 2027 application with the same old name, and another org's domain organisation.
    private async Task<(ObjectId Approved, ObjectId Draft)> SeedOldStateAsync()
    {
        var approved = await SeedAsync(Application());
        var draft = await SeedAsync(
            Application(
                orgId: null,
                registration: null,
                accreditation: null,
                status: ApplicationStatus.Saved
            )
        );
        await SeedAsync(Application(year: 2026, accreditation: Accreditation2026));
        await SeedAsync(
            Application(
                orgId: 500070,
                reExId: "reex-500070",
                registration: "R26NR5000700001GL",
                accreditation: "A27NR5000700001GL"
            )
        );
        await SeedOrganisationAsync(reExId: "reex-500070", number: 500070);
        return (approved, draft);
    }

    private OrganisationNameCorrectionMigration CreateMigration(
        bool enabled,
        bool apply = false,
        string? confirmedBy = "A Reviewer",
        bool registerPersistence = true,
        bool registerOrganisations = true
    )
    {
        var services = new ServiceCollection();
        if (registerPersistence)
            services.AddSingleton<IAccreditationApplicationPersistence>(_persistence);
        if (registerOrganisations)
            services.AddSingleton(_organisations);
        return new OrganisationNameCorrectionMigration(
            services.BuildServiceProvider(),
            Options.Create(
                new OrganisationNameCorrectionOptions
                {
                    Enabled = enabled,
                    Apply = apply,
                    SpotCheckConfirmedBy = confirmedBy,
                }
            ),
            _logger
        );
    }

    private Task<RunResult> RunAsync(OrganisationNameCorrectionMigration migration) =>
        migration.RunAsync(TestContext.Current.CancellationToken);

    private async Task<List<BsonDocument>> SnapshotAsync()
    {
        var all = new List<BsonDocument>();
        foreach (var collection in new[] { _raw, _rawOrganisations })
            all.AddRange(
                await collection
                    .Find(FilterDefinition<BsonDocument>.Empty)
                    .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                    .ToListAsync(TestContext.Current.CancellationToken)
            );
        return all;
    }

    private async Task<BsonDocument> ReadRawAsync(
        IMongoCollection<BsonDocument> collection,
        ObjectId id
    ) =>
        await collection
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id))
            .FirstAsync(TestContext.Current.CancellationToken);

    private IEnumerable<string> Logged(LogLevel level) =>
        _logger.Entries.Where(e => e.LogLevel == level).Select(e => e.Message);

    private async Task AssertAbortsWithoutWritingAsync(string expectedError)
    {
        var before = await SnapshotAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Aborted);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().Contain(m => m.Contains(expectedError));
        Logged(LogLevel.Error).Should().Contain(m => m.Contains("nothing has been written"));
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains("APPLIED"));
    }

    [Fact]
    public async Task Disabled_is_a_no_op_that_never_resolves_the_persistence()
    {
        await SeedOldStateAsync();
        await SeedOrganisationAsync();
        var before = await SnapshotAsync();

        // No persistence registered: resolving it would throw.
        var result = await RunAsync(
            CreateMigration(enabled: false, apply: true, registerPersistence: false)
        );

        result.Should().Be(RunResult.Disabled);
        (await SnapshotAsync()).Should().Equal(before);
        _logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Dry_run_logs_each_rename_and_writes_nothing()
    {
        var (approved, draft) = await SeedOldStateAsync();
        var organisation = await SeedOrganisationAsync();
        var before = await SnapshotAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: false));

        result.Should().Be(RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        var renames = Logged(LogLevel.Information)
            .Where(m => m.Contains($"\"{OldName}\" -> \"{NewName}\""))
            .ToList();
        renames.Should().HaveCount(3).And.OnlyContain(m => m.Contains("DRY RUN"));
        renames.Should().ContainSingle(m => m.Contains(approved.ToString()));
        renames.Should().ContainSingle(m => m.Contains(draft.ToString()));
        renames.Should().ContainSingle(m => m.Contains(organisation.ToString()));
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_without_a_spot_check_runs_as_a_dry_run_and_writes_nothing()
    {
        await SeedOldStateAsync();
        await SeedOrganisationAsync();
        var before = await SnapshotAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true, confirmedBy: " "));

        result.Should().Be(RunResult.DryRun);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Error).Should().ContainSingle(m => m.Contains("SpotCheckConfirmedBy"));
    }

    [Fact]
    public async Task Apply_renames_every_2027_application_and_the_domain_organisation()
    {
        var (approved, draft) = await SeedOldStateAsync();
        var organisation = await SeedOrganisationAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await ReadRawAsync(_raw, approved))["organisationName"].AsString.Should().Be(NewName);
        (await ReadRawAsync(_raw, draft))["organisationName"].AsString.Should().Be(NewName);
        (await ReadRawAsync(_rawOrganisations, organisation))["name"]
            .AsString.Should()
            .Be(NewName);
        Logged(LogLevel.Information)
            .Where(m => m.StartsWith("RA-764 APPLIED"))
            .Should()
            .HaveCount(3)
            .And.OnlyContain(m => m.Contains("A Reviewer"));
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    // AC2: status, references, history and the edit date are left exactly as they were, and no
    // audit entry is added. UpdatedAt and Version move, as on every targeted legacy update.
    [Fact]
    public async Task Apply_changes_nothing_but_the_name()
    {
        var (approved, draft) = await SeedOldStateAsync();
        var organisation = await SeedOrganisationAsync();
        var before = (await SnapshotAsync()).ToDictionary(d => d["_id"].AsObjectId);

        await RunAsync(CreateMigration(enabled: true, apply: true));

        foreach (var id in new[] { approved, draft })
        {
            var after = await ReadRawAsync(_raw, id);
            Without(after, "organisationName", "updatedAt", "version")
                .Should()
                .Equal(Without(before[id], "organisationName", "updatedAt", "version"));
            after["version"].ToInt64().Should().Be(before[id]["version"].ToInt64() + 1);
        }
        Without(await ReadRawAsync(_rawOrganisations, organisation), "name")
            .Should()
            .Equal(Without(before[organisation], "name"));
    }

    [Fact]
    public async Task Apply_changes_no_other_record()
    {
        var (approved, draft) = await SeedOldStateAsync();
        var organisation = await SeedOrganisationAsync();
        var targets = new[] { approved, draft, organisation };
        var before = (await SnapshotAsync())
            .Where(d => !targets.Contains(d["_id"].AsObjectId))
            .ToList();

        await RunAsync(CreateMigration(enabled: true, apply: true));

        (await SnapshotAsync())
            .Where(d => !targets.Contains(d["_id"].AsObjectId))
            .Should()
            .Equal(before);
    }

    [Fact]
    public async Task Re_running_after_an_apply_writes_nothing_and_skips_every_record()
    {
        await SeedOldStateAsync();
        await SeedOrganisationAsync();
        await RunAsync(CreateMigration(enabled: true, apply: true));
        var afterFirstRun = await SnapshotAsync();
        _logger.Entries.Clear();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await SnapshotAsync()).Should().Equal(afterFirstRun);
        Logged(LogLevel.Information)
            .Where(m => m.Contains("already holds"))
            .Should()
            .HaveCount(3);
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains(" -> "));
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Already_renamed_records_are_logged_and_left_alone()
    {
        await SeedAsync(Application(name: NewName));
        await SeedOrganisationAsync(name: NewName);
        var before = await SnapshotAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await SnapshotAsync()).Should().Equal(before);
        Logged(LogLevel.Information)
            .Where(m => m.Contains("already holds"))
            .Should()
            .HaveCount(2);
    }

    [Fact]
    public async Task No_domain_model_organisation_is_skipped_cleanly()
    {
        var (approved, _) = await SeedOldStateAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await ReadRawAsync(_raw, approved))["organisationName"].AsString.Should().Be(NewName);
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m => m.Contains("no domain model organisation"));
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Without_the_domain_model_schema_the_organisation_is_skipped_cleanly()
    {
        var (approved, _) = await SeedOldStateAsync();
        var organisation = await SeedOrganisationAsync();

        var result = await RunAsync(
            CreateMigration(enabled: true, apply: true, registerOrganisations: false)
        );

        result.Should().Be(RunResult.Applied);
        (await ReadRawAsync(_raw, approved))["organisationName"].AsString.Should().Be(NewName);
        (await ReadRawAsync(_rawOrganisations, organisation))["name"]
            .AsString.Should()
            .Be(OldName);
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task A_domain_organisation_without_a_number_is_matched_by_its_ReEx_id()
    {
        await SeedOldStateAsync();
        var organisation = await SeedOrganisationAsync(number: null);

        await RunAsync(CreateMigration(enabled: true, apply: true));

        (await ReadRawAsync(_rawOrganisations, organisation))["name"]
            .AsString.Should()
            .Be(NewName);
    }

    [Fact]
    public async Task No_2027_application_aborts()
    {
        await SeedAsync(Application(year: 2026, accreditation: Accreditation2026));
        await SeedOrganisationAsync();

        await AssertAbortsWithoutWritingAsync("no 2027 application found");
    }

    [Fact]
    public async Task An_unexpected_name_aborts()
    {
        await SeedOldStateAsync();
        await SeedAsync(Application(name: "Encirc Limited", accreditation: null));

        await AssertAbortsWithoutWritingAsync("Encirc Limited");
    }

    [Fact]
    public async Task A_reference_for_another_organisation_aborts()
    {
        await SeedOldStateAsync();
        await SeedAsync(Application(accreditation: "A27NR5000700003GL"));

        await AssertAbortsWithoutWritingAsync("A27NR5000700003GL");
    }

    [Fact]
    public async Task Applications_under_two_ReEx_organisation_ids_abort()
    {
        await SeedOldStateAsync();
        await SeedAsync(Application(reExId: "reex-other"));

        await AssertAbortsWithoutWritingAsync("different ReEx organisation ids");
    }

    [Fact]
    public async Task No_application_holding_the_named_registration_aborts()
    {
        await SeedAsync(Application(registration: "R26NR5000690004GL"));

        await AssertAbortsWithoutWritingAsync(Registration);
    }

    [Fact]
    public async Task Two_matching_domain_organisations_abort()
    {
        await SeedOldStateAsync();
        // One held under 500069's ReEx id but not yet numbered, another numbered 500069 under a
        // different ReEx id: both keys match, on different documents.
        await SeedOrganisationAsync(number: null);
        await SeedOrganisationAsync(reExId: "reex-duplicate");

        await AssertAbortsWithoutWritingAsync("expected at most one");
    }

    [Fact]
    public async Task A_domain_organisation_with_an_unexpected_name_aborts()
    {
        await SeedOldStateAsync();
        await SeedOrganisationAsync(name: "Someone Else Ltd");

        await AssertAbortsWithoutWritingAsync("Someone Else Ltd");
    }

    private static BsonDocument Without(BsonDocument document, params string[] fields)
    {
        var copy = document.DeepClone().AsBsonDocument;
        foreach (var field in fields)
            copy.Remove(field);
        return copy;
    }
}
