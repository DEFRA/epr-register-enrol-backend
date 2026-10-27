using EphemeralMongo;
using EprRegisterEnrolBackend.DomainModel.Diagnostics;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using EprRegisterEnrolBackend.Utils.Mongo;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.DomainModel.Diagnostics;

/// <summary>B0 (transaction smoke test) and B1 (legacy data survey): the test-environment-only diagnostics.</summary>
public sealed class DomainModelDiagnosticsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly IMongoCollection<BsonDocument> _applications;

    public DomainModelDiagnosticsTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("diagnostics");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _applications = _factory.GetCollection<BsonDocument>("accreditationApplications");
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static BsonDocument App(params (string Name, BsonValue Value)[] elements)
    {
        var doc = new BsonDocument
        {
            { "organisationId", "org" },
            { "year", 2027 },
            { "materialType", "Plastic" },
            { "applicationStatus", "Submitted" },
        };
        foreach (var (name, value) in elements)
        {
            doc[name] = value;
        }

        return doc;
    }

    private static BsonDocument Sites(params BsonDocument[] sites) =>
        new("sites", new BsonArray(sites));

    // ---- B0

    [Fact]
    public async Task The_smoke_test_passes_on_a_replica_set_and_leaves_nothing_behind()
    {
        var result = await TransactionSmokeTest.RunAsync(
            _factory.GetCollection<BsonDocument>("x").Database,
            Ct
        );

        result.Supported.Should().BeTrue();
        result.ErrorKind.Should().BeNull();
        (
            await _factory
                .GetCollection<BsonDocument>(TransactionSmokeTest.ScratchCollection)
                .CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct)
        )
            .Should()
            .Be(0);
    }

    [Fact]
    public async Task The_smoke_test_reports_unsupported_on_a_standalone_server()
    {
        using var standalone = await MongoRunner.RunAsync(
            new MongoRunnerOptions { UseSingleNodeReplicaSet = false },
            Ct
        );
        var database = new MongoClient(standalone.ConnectionString).GetDatabase("smoke");

        var result = await TransactionSmokeTest.RunAsync(database, Ct);

        result.Supported.Should().BeFalse();
        result.ErrorKind.Should().NotBeNullOrEmpty().And.Be("NotSupportedException");
    }

    // ---- B1: each known legacy shape is counted

    [Fact]
    public async Task The_survey_of_an_empty_collection_is_all_zeros()
    {
        var report = await LegacyDataSurvey.RunAsync(_applications, Ct);

        report.Should().Be(new LegacyDataSurveyReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task The_survey_counts_int32_enums()
    {
        await _applications.InsertManyAsync(
            [App(("applicationStatus", 4)), App(("materialType", 2)), App()],
            cancellationToken: Ct
        );

        (await LegacyDataSurvey.RunAsync(_applications, Ct)).Int32EnumDocuments.Should().Be(2);
    }

    [Fact]
    public async Task The_survey_counts_singular_interimSite_only_documents()
    {
        var singular = new BsonDocument
        {
            { "siteId", 1 },
            { "interimSite", new BsonDocument("siteNumber", "001") },
        };
        var withList = new BsonDocument
        {
            { "siteId", 1 },
            { "interimSite", new BsonDocument("siteNumber", "001") },
            {
                "interimSites",
                new BsonArray { new BsonDocument("siteNumber", "001") }
            },
        };
        await _applications.InsertManyAsync(
            [
                App(("overseasSites", Sites(singular))),
                App(("overseasSites", Sites(withList))),
                App(),
            ],
            cancellationToken: Ct
        );

        (await LegacyDataSurvey.RunAsync(_applications, Ct))
            .InterimSiteSingularOnlyDocuments.Should()
            .Be(1);
    }

    [Fact]
    public async Task The_survey_counts_null_orsIds_and_legacy_SN_interim_numbers()
    {
        var nullOrs = new BsonDocument { { "siteId", 1 }, { "orsId", BsonNull.Value } };
        var missingOrs = new BsonDocument("siteId", 2);
        var withOrs = new BsonDocument { { "siteId", 3 }, { "orsId", "001" } };
        var snInList = new BsonDocument
        {
            { "siteId", 4 },
            { "orsId", "002" },
            {
                "interimSites",
                new BsonArray { new BsonDocument("siteNumber", "SN-0007") }
            },
        };
        var snSingular = new BsonDocument
        {
            { "siteId", 5 },
            { "orsId", "003" },
            { "interimSite", new BsonDocument("siteNumber", "SN-0008") },
        };
        await _applications.InsertManyAsync(
            [
                App(("overseasSites", Sites(nullOrs))),
                App(("overseasSites", Sites(missingOrs))),
                App(("overseasSites", Sites(withOrs))),
                App(("overseasSites", Sites(snInList))),
                App(("overseasSites", Sites(snSingular))),
            ],
            cancellationToken: Ct
        );

        var report = await LegacyDataSurvey.RunAsync(_applications, Ct);

        report.NullOrsIdDocuments.Should().Be(2);
        report.LegacySnInterimNumberDocuments.Should().Be(2);
    }

    [Fact]
    public async Task The_survey_counts_duplicate_live_applications_but_not_withdrawn_ones()
    {
        await _applications.InsertManyAsync(
            [
                App(("registrationId", "r1")),
                App(("registrationId", "r1")),
                App(("registrationId", "r2")),
                App(("registrationId", "r2"), ("applicationStatus", "Withdrawn")),
                App(("registrationId", "r3"), ("year", 2026)),
                App(("registrationId", "r3")),
            ],
            cancellationToken: Ct
        );

        (await LegacyDataSurvey.RunAsync(_applications, Ct))
            .DuplicateLiveApplicationGroups.Should()
            .Be(1);
    }

    [Fact]
    public async Task The_survey_counts_registrations_with_conflicting_registration_numbers()
    {
        await _applications.InsertManyAsync(
            [
                App(("registrationId", "r1"), ("registrationReference", "R27EX0000001P")),
                App(("registrationId", "r1"), ("registrationReference", "R27EX0000002P")),
                App(("registrationId", "r2"), ("registrationReference", "R27EX0000003P")),
                App(("registrationId", "r2"), ("registrationReference", "R27EX0000003P")),
                App(("registrationId", "r3")),
            ],
            cancellationToken: Ct
        );

        (await LegacyDataSurvey.RunAsync(_applications, Ct))
            .ConflictingRegistrationNumberGroups.Should()
            .Be(1);
    }

    [Fact]
    public async Task The_survey_counts_approved_applications_with_no_accreditation_reference()
    {
        await _applications.InsertManyAsync(
            [
                App(("applicationStatus", "Approved")),
                App(("applicationStatus", "Approved"), ("accreditationReference", BsonNull.Value)),
                App(("applicationStatus", "Approved"), ("accreditationReference", "A27EX0000001P")),
                App(("accreditationReference", "A27EX0000002P")),
            ],
            cancellationToken: Ct
        );

        (await LegacyDataSurvey.RunAsync(_applications, Ct))
            .ApprovedWithoutAccreditationReference.Should()
            .Be(2);
    }

    [Fact]
    public async Task The_survey_counts_case_management_references_and_duplicate_accreditation_references()
    {
        await _applications.InsertManyAsync(
            [
                App(
                    ("caseManagementReference", "REF-1"),
                    ("accreditationReference", "A27EX0000001P")
                ),
                App(("caseManagementReference", ""), ("accreditationReference", "A27EX0000001P")),
                App(("accreditationReference", "A27EX0000002P")),
            ],
            cancellationToken: Ct
        );

        var report = await LegacyDataSurvey.RunAsync(_applications, Ct);

        report.CaseManagementReferencePresent.Should().Be(1);
        report.DuplicateAccreditationReferenceGroups.Should().Be(1);
    }

    [Fact]
    public async Task The_survey_does_not_write_and_its_log_line_contains_counts_only()
    {
        await _applications.InsertOneAsync(
            App(("organisationName", "Secret Trading Name Ltd"), ("registrationId", "r1")),
            cancellationToken: Ct
        );
        var before = await _applications.CountDocumentsAsync(
            new BsonDocument(),
            cancellationToken: Ct
        );

        var report = await LegacyDataSurvey.RunAsync(_applications, Ct);

        (await _applications.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct))
            .Should()
            .Be(before);
        report.ToLogLine().Should().NotContain("Secret").And.MatchRegex(@"^(\w+=\d+ ?)+$");
    }

    // ---- the service

    private static DomainModelDiagnosticsService Service(
        IMongoDbClientFactory factory,
        bool enabled,
        ILogger<DomainModelDiagnosticsService> logger
    ) =>
        new(
            factory,
            Options.Create(new DomainModelDiagnosticsConfig { Enabled = enabled }),
            logger
        );

    [Fact]
    public async Task The_service_does_nothing_unless_enabled()
    {
        var factory = Substitute.For<IMongoDbClientFactory>();
        var logger = new CapturingLogger<DomainModelDiagnosticsService>();

        await Service(factory, enabled: false, logger).RunAsync(Ct);

        factory.ReceivedCalls().Should().BeEmpty();
        logger.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task When_enabled_the_service_logs_one_transaction_line_and_one_survey_line()
    {
        await _applications.InsertOneAsync(
            App(("organisationName", "Secret Trading Name Ltd"), ("applicationStatus", 3)),
            cancellationToken: Ct
        );
        var logger = new CapturingLogger<DomainModelDiagnosticsService>();

        await Service(_factory, enabled: true, logger).RunAsync(Ct);

        logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
        var lines = logger.Entries.Select(e => e.Message).ToList();
        lines.Should().HaveCount(2);
        lines.Should().Contain(l => l.Contains("transactions supported=true"));
        lines
            .Should()
            .Contain(l =>
                l.Contains("survey") && l.Contains("applications=1") && l.Contains("int32Enums=1")
            );
        lines.Should().NotContain(l => l.Contains("Secret"));
    }

    [Fact]
    public async Task A_failure_is_logged_as_a_short_error_and_does_not_throw()
    {
        var factory = Substitute.For<IMongoDbClientFactory>();
        factory
            .GetCollection<BsonDocument>(Arg.Any<string>())
            .Returns(_ => throw new TimeoutException("mongodb://user:secret@host"));
        var logger = new CapturingLogger<DomainModelDiagnosticsService>();

        await Service(factory, enabled: true, logger).RunAsync(Ct);

        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Error);
        entry.Message.Should().Contain("TimeoutException").And.NotContain("secret");
    }
}
