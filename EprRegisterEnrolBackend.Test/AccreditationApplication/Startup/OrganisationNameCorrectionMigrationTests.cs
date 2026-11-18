using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
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
    // 500069's two registrations (glass remelt): NIEA, whose references carry 500069, and EA, whose
    // references carry 500226.
    private const string Registration = "R26NR5000690003GR";
    private const string Accreditation2026 = "A26NR5000690003GR";
    private const string Accreditation2027 = "A27NR5000690003GR";
    private const string EaRegistration = "R26ER5002260055GR";
    private const string EaAccreditation2027 = "A27ER5002260023GR";
    private const string ApplicationsCollection = "accreditationApplications";

    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _persistence;
    private readonly OrganisationPersistence _organisations;
    private readonly MigrationConflictPersistence _conflicts;
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
        _conflicts = new MigrationConflictPersistence(_factory, NullLoggerFactory.Instance);
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
            PreviousRegistrationNumbers = ["R25NR5000690003GR"],
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
        var (approved, draft, _) = await SeedOldStateWith2026Async();
        return (approved, draft);
    }

    private async Task<(ObjectId Approved, ObjectId Draft, ObjectId Previous)> SeedOldStateWith2026Async()
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
        var previous = await SeedAsync(Application(year: 2026, accreditation: Accreditation2026));
        await SeedAsync(
            Application(
                orgId: 500070,
                reExId: "reex-500070",
                registration: "R26NR5000700001GR",
                accreditation: "A27NR5000700001GR"
            )
        );
        await SeedOrganisationAsync(reExId: "reex-500070", number: 500070);
        return (approved, draft, previous);
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
        {
            services.AddSingleton(_organisations);
            services.AddSingleton(_conflicts);
        }
        services.AddSingleton<IMongoDbClientFactory>(_factory);
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

    private static Task<RunResult> RunAsync(OrganisationNameCorrectionMigration migration) =>
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

    private static async Task<BsonDocument> ReadRawAsync(
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
            .Where(m => m.StartsWith("RA-764 APPLIED") && !m.Contains("conflict"))
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
        await SeedAsync(Application(accreditation: "A27NR5000700003GR"));

        await AssertAbortsWithoutWritingAsync("A27NR5000700003GR");
    }

    [Fact]
    public async Task Applications_under_two_ReEx_organisation_ids_abort()
    {
        await SeedOldStateAsync();
        await SeedAsync(Application(reExId: "reex-other"));

        await AssertAbortsWithoutWritingAsync("different ReEx organisation ids");
    }

    [Fact]
    public async Task No_application_holding_an_allowed_registration_aborts()
    {
        await SeedAsync(Application(registration: null, accreditation: null));

        await AssertAbortsWithoutWritingAsync(
            $"holds registration {Registration} or {EaRegistration}"
        );
    }

    // RA-764 prod dry run, 17 Nov: aborted on exactly this record, as its EA reference carries 500226.
    [Fact]
    public async Task An_EA_registration_application_is_renamed_not_aborted()
    {
        var ea = await SeedAsync(Application(registration: EaRegistration, accreditation: null));
        var organisation = await SeedOrganisationAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await ReadRawAsync(_raw, ea))["organisationName"].AsString.Should().Be(NewName);
        (await ReadRawAsync(_rawOrganisations, organisation))["name"]
            .AsString.Should()
            .Be(NewName);
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    private async Task<ObjectId[]> SeedBothRegistrationsAsync() =>
        [
            await SeedAsync(Application()),
            await SeedAsync(Application(registration: EaRegistration, accreditation: null)),
            await SeedAsync(
                Application(registration: EaRegistration, accreditation: EaAccreditation2027)
            ),
        ];

    [Fact]
    public async Task Both_registrations_are_renamed_in_one_run()
    {
        var applications = await SeedBothRegistrationsAsync();
        var organisation = await SeedOrganisationAsync();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        foreach (var id in applications)
            (await ReadRawAsync(_raw, id))["organisationName"].AsString.Should().Be(NewName);
        (await ReadRawAsync(_rawOrganisations, organisation))["name"]
            .AsString.Should()
            .Be(NewName);
        Logged(LogLevel.Information)
            .Where(m => m.StartsWith("RA-764 APPLIED") && !m.Contains("conflict"))
            .Should()
            .HaveCount(4);
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task Re_running_over_both_registrations_writes_nothing()
    {
        await SeedBothRegistrationsAsync();
        await SeedOrganisationAsync();
        await RunAsync(CreateMigration(enabled: true, apply: true));
        var afterFirstRun = await SnapshotAsync();
        _logger.Entries.Clear();

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await SnapshotAsync()).Should().Equal(afterFirstRun);
        Logged(LogLevel.Information).Where(m => m.Contains("already holds")).Should().HaveCount(4);
        Logged(LogLevel.Information).Should().NotContain(m => m.Contains(" -> "));
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    // Derived for 2027 from each allowed registration and its 2026 accreditation
    // (A26NR5000690003GR, A26ER5002260023GR), plus a sequence other than the derived one; a draft
    // with no registration yet may hold either registration's shape.
    [Theory]
    [InlineData(Registration, "A27NR5000690003GR")]
    [InlineData(EaRegistration, "A27ER5002260055GR")]
    [InlineData(EaRegistration, "A27ER5002260023GR")]
    [InlineData(Registration, "A27NR5000690099GR")]
    [InlineData(EaRegistration, "A27ER5002260001GR")]
    [InlineData(null, "A27ER5002260023GR")]
    [InlineData(null, "A27NR5000690003GR")]
    public async Task An_accreditation_for_an_allowed_registration_is_accepted(
        string? registration,
        string accreditation
    )
    {
        await SeedAsync(Application());
        var target = await SeedAsync(
            Application(registration: registration, accreditation: accreditation)
        );

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await ReadRawAsync(_raw, target))["organisationName"].AsString.Should().Be(NewName);
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Theory]
    // Registrations outside the allow-list: the ticket's original GL suffix, another sequence,
    // another agency.
    [InlineData("R26NR5000690003GL", null)]
    [InlineData("R26ER5002260056GR", null)]
    [InlineData("R26WR5000690003GR", null)]
    // Accreditations that are not for the record's own registration.
    [InlineData(Registration, "A27ER5002260023GR")]
    [InlineData(EaRegistration, "A27NR5000690003GR")]
    [InlineData(Registration, "A27NR5000690003GL")]
    [InlineData(Registration, "A26NR5000690003GR")]
    [InlineData(Registration, "A27NR50006900X3GR")]
    [InlineData(null, "A27NR5000700003GR")]
    public async Task A_reference_outside_the_allow_list_aborts(
        string? registration,
        string? accreditation
    )
    {
        await SeedOldStateAsync();
        await SeedOrganisationAsync();
        await SeedAsync(Application(registration: registration, accreditation: accreditation));

        await AssertAbortsWithoutWritingAsync(
            $"registration {registration ?? "(null)"}, accreditation {accreditation ?? "(null)"}, "
        );
    }

    // Review of PR #271: neither the OrgId nor the ReEx id lookup returns a record under one of the
    // registrations holding another OrgId or ReEx organisation id, so it would have been silently
    // left on the old name. Found by its registration, it aborts the run instead.
    [Theory]
    [InlineData(500226, ReExId)]
    [InlineData(null, "reex-500226")]
    [InlineData(500226, "reex-500226")]
    public async Task An_application_under_a_registration_for_another_OrgId_or_ReEx_id_aborts(
        int? orgId,
        string reExId
    )
    {
        await SeedOldStateAsync();
        await SeedOrganisationAsync();
        var stray = await SeedAsync(
            Application(
                orgId: orgId,
                reExId: reExId,
                registration: EaRegistration,
                accreditation: null
            )
        );

        await AssertAbortsWithoutWritingAsync(
            $"application {stray} is Year 2027, OrgId {orgId?.ToString() ?? "(null)"}, ReEx "
                + $"organisation {reExId}, status Approved, registration {EaRegistration}"
        );
        Logged(LogLevel.Error)
            .Should()
            .Contain(m => m.Contains($"ReEx organisation {ReExId}, registration null or one"));
    }

    [Fact]
    public async Task Another_years_application_under_a_registration_is_not_looked_at()
    {
        var target = await SeedAsync(Application());
        var other = await SeedAsync(
            Application(year: 2026, orgId: 500226, registration: EaRegistration, accreditation: null)
        );

        var result = await RunAsync(CreateMigration(enabled: true, apply: true));

        result.Should().Be(RunResult.Applied);
        (await ReadRawAsync(_raw, target))["organisationName"].AsString.Should().Be(NewName);
        (await ReadRawAsync(_raw, other))["organisationName"].AsString.Should().Be(OldName);
    }

    private string Completion() =>
        Logged(LogLevel.Information)
            .Should()
            .ContainSingle(m => m.Contains("organisation name correction complete"))
            .Subject;

    // Review of PR #271: the spot check counts the applications per registration.
    [Fact]
    public async Task The_completion_log_counts_the_applications_per_registration()
    {
        var applications = await SeedBothRegistrationsAsync();
        await SeedAsync(Application(registration: null, accreditation: null, orgId: null));
        await SeedOrganisationAsync();

        await RunAsync(CreateMigration(enabled: true));
        Completion()
            .Should()
            .Contain(
                $"{Registration}: 1 planned, 0 skipped (already renamed); {EaRegistration}: 2 "
                    + "planned, 0 skipped (already renamed); no registration: 1 planned, 0 skipped"
            );
        Logged(LogLevel.Information)
            .Should()
            .Contain(m =>
                m.Contains(applications[1].ToString())
                && m.Contains($"registration {EaRegistration}, accreditation (null), status Approved)")
            );

        _logger.Entries.Clear();
        await _persistence.UpdateFieldsAsync(
            applications[2],
            Builders<AccreditationApplicationModel>.Filter.Empty,
            Builders<AccreditationApplicationModel>.Update.Set(a => a.OrganisationName, NewName),
            cancellationToken: TestContext.Current.CancellationToken
        );
        await RunAsync(CreateMigration(enabled: true, apply: true));
        Completion()
            .Should()
            .Contain(
                $"{Registration}: 1 applied, 0 skipped (already renamed), 0 not written; "
                    + $"{EaRegistration}: 1 applied, 1 skipped (already renamed), 0 not written; "
                    + "no registration: 1 applied, 0 skipped (already renamed), 0 not written"
            );
    }

    // Prod has no data access, so the abort log is the only evidence of the unexpected record.
    [Fact]
    public async Task The_abort_log_names_the_records_keys_and_status()
    {
        await SeedAsync(Application());
        var unexpected = await SeedAsync(
            Application(
                registration: "R26ER5002260056GR",
                accreditation: null,
                status: ApplicationStatus.Submitted
            )
        );

        await AssertAbortsWithoutWritingAsync(
            $"application {unexpected} is Year 2027, OrgId 500069, ReEx organisation {ReExId}, "
                + "status Submitted, registration R26ER5002260056GR"
        );
        Logged(LogLevel.Error)
            .Should()
            .Contain(m =>
                m.Contains($"one of {Registration}, {EaRegistration}")
                && m.Contains("A27NR500069####GR")
                && m.Contains("A27ER500226####GR")
            );
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

    private async Task<List<MigrationConflict>> ConflictsAsync() =>
        await _factory
            .GetCollection<MigrationConflict>(DomainModelCollections.MigrationConflicts)
            .Find(FilterDefinition<MigrationConflict>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);

    // Review of PR #257: the Organisation is organisation-wide, so once renamed the read model shows
    // the new name on 500069's 2026 application too. A SeededDetailsDiffer conflict is what lets the
    // reconciler accept that difference from the 2026 legacy document.
    [Fact]
    public async Task Apply_records_one_SeededDetailsDiffer_conflict_per_other_year_application()
    {
        var (_, _, previous) = await SeedOldStateWith2026Async();
        await SeedOrganisationAsync();

        await RunAsync(CreateMigration(enabled: true, apply: true));
        await RunAsync(CreateMigration(enabled: true, apply: true));

        var conflict = (await ConflictsAsync()).Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(MigrationConflictKind.SeededDetailsDiffer);
        conflict.ApplicationIds.Should().Equal(previous);
        Logged(LogLevel.Information).Should().Contain(m => m.Contains("already recorded"));
        Logged(LogLevel.Error).Should().BeEmpty();
    }

    [Fact]
    public async Task An_existing_SeededDetailsDiffer_conflict_is_not_duplicated()
    {
        var (_, _, previous) = await SeedOldStateWith2026Async();
        await SeedOrganisationAsync();
        await _factory
            .GetCollection<MigrationConflict>(DomainModelCollections.MigrationConflicts)
            .InsertOneAsync(
                new MigrationConflict
                {
                    Kind = MigrationConflictKind.SeededDetailsDiffer,
                    ReExRegistrationId = "",
                    ApplicationIds = [previous],
                },
                cancellationToken: TestContext.Current.CancellationToken
            );

        await RunAsync(CreateMigration(enabled: true, apply: true));

        (await ConflictsAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Dry_run_records_no_conflict_but_logs_it()
    {
        await SeedOldStateAsync();
        await SeedOrganisationAsync();

        await RunAsync(CreateMigration(enabled: true));

        (await ConflictsAsync()).Should().BeEmpty();
        Logged(LogLevel.Information).Should().ContainSingle(m => m.Contains("to record"));
    }

    [Fact]
    public async Task Without_a_domain_organisation_no_conflict_is_recorded()
    {
        await SeedOldStateAsync();

        await RunAsync(CreateMigration(enabled: true, apply: true));

        (await ConflictsAsync()).Should().BeEmpty();
    }

    [Fact]
    public void The_shipped_appsettings_has_the_migration_off_and_logs_it_at_information()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

        configuration
            .GetSection(OrganisationNameCorrectionOptions.SectionName)
            .Get<OrganisationNameCorrectionOptions>()
            .Should()
            .BeEquivalentTo(
                new OrganisationNameCorrectionOptions
                {
                    Enabled = false,
                    Apply = false,
                    SpotCheckConfirmedBy = "",
                }
            );
        // CDP defaults to Warning, so without this a successful dry run or apply logs nothing
        // in ext-test or prod.
        configuration[
            "Serilog:MinimumLevel:Override:EprRegisterEnrolBackend.AccreditationApplication.Startup.OrganisationNameCorrectionMigration"
        ]
            .Should()
            .Be("Information");
    }

    private static BsonDocument Without(BsonDocument document, params string[] fields)
    {
        var copy = document.DeepClone().AsBsonDocument;
        foreach (var field in fields)
            copy.Remove(field);
        return copy;
    }
}
