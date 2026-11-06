using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Entities;

/// <summary>
/// The Application's <c>details</c> is polymorphic (option C1, register-enrol-application-types.md): these tests prove
/// the driver round-trips it through the versioned repository, whose writes copy the entity through BSON and compare
/// stored forms, and that the stored discriminator agrees with the top-level <c>applicationType</c>.
/// </summary>
public sealed class ApplicationDetailsRoundTripTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestClock _clock = new(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
    private readonly ApplicationVersionPersistence _applications;
    private readonly IMongoCollection<BsonDocument> _raw;

    public ApplicationDetailsRoundTripTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("details");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _applications = new ApplicationVersionPersistence(
            _factory,
            NullLoggerFactory.Instance,
            _clock
        );
        _raw = _factory.GetCollection<BsonDocument>(DomainModelCollections.ApplicationVersions);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static ChangeInfo Change() => new() { Reason = ChangeReason.ApplicationEdited };

    [Fact]
    public async Task Insert_AccreditationApplication_ReadsBackItsDetails()
    {
        var created = await _applications.Versions.InsertAsync(
            DomainModelBuilders.Application(),
            Change(),
            cancellationToken: Ct
        );

        var read = await _applications.Versions.GetCurrentAsync(
            created.EntityId,
            cancellationToken: Ct
        );

        read!.ApplicationType.Should().Be(ApplicationType.Accreditation);
        var details = read.Details.Should().BeOfType<AccreditationDetails>().Subject;
        details.Kind.Should().Be(AccreditationKind.Reapply);
        details.Year.Should().Be(2027);
        details.Prns.PlannedTonnageBand.Should().Be(TonnageBand.UpTo10000);
        details.SamplingPlan.Files.Should().ContainSingle();
        details.ApplicationOverseasSites.Should().ContainSingle(e => e.SiteId == 1);
    }

    [Fact]
    public async Task Insert_AccreditationApplication_StoresTheTypeAtTheTopAndTheFieldsUnderDetails()
    {
        var created = await _applications.Versions.InsertAsync(
            DomainModelBuilders.Application(),
            Change(),
            cancellationToken: Ct
        );

        var stored = await _raw.Find(new BsonDocument("_id", created.Id)).SingleAsync(Ct);

        stored["applicationType"].AsString.Should().Be("Accreditation");
        stored["details"]["_t"].AsString.Should().Be(stored["applicationType"].AsString);
        stored["details"]["year"].AsInt32.Should().Be(2027);
        stored
            .Names.Should()
            .NotContain([
                "year",
                "prns",
                "businessPlan",
                "samplingPlan",
                "applicationOverseasSites",
            ]);
    }

    [Fact]
    public async Task Update_ChangeOnlyInsideDetails_WritesANewVersionOfTheSameType()
    {
        var created = await _applications.Versions.InsertAsync(
            DomainModelBuilders.Application(),
            Change(),
            cancellationToken: Ct
        );
        created.AsAccreditation().BusinessPlan.NewInfrastructurePercent = 40;

        var result = await _applications.Versions.UpdateAsync(
            created,
            created.Version,
            Change(),
            cancellationToken: Ct
        );

        result.Changed.Should().BeTrue();
        var versions = await _applications.Versions.GetVersionsAsync(
            created.EntityId,
            cancellationToken: Ct
        );
        versions.Select(v => v.Details).Should().AllBeOfType<AccreditationDetails>();
        versions
            .Select(v => v.AsAccreditation().BusinessPlan.NewInfrastructurePercent)
            .Should()
            .Equal(100, 40);
    }

    [Fact]
    public async Task Update_NothingChanged_WritesNoVersion()
    {
        var created = await _applications.Versions.InsertAsync(
            DomainModelBuilders.Application(),
            Change(),
            cancellationToken: Ct
        );

        var result = await _applications.Versions.UpdateAsync(
            created,
            created.Version,
            Change(),
            cancellationToken: Ct
        );

        result.Changed.Should().BeFalse();
        (
            await _raw.CountDocumentsAsync(
                new BsonDocument("entityId", created.EntityId),
                cancellationToken: Ct
            )
        )
            .Should()
            .Be(1);
    }
}
