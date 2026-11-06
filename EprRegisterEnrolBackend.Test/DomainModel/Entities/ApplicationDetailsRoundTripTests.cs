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

    // --- the other four types -----------------------------------------------------------------------------------

    private async Task<(ApplicationEntity Read, BsonDocument Stored)> RoundTripAsync(
        ApplicationDetails details
    )
    {
        var created = await _applications.Versions.InsertAsync(
            DomainModelBuilders.Application(ObjectId.GenerateNewId(), details),
            Change(),
            cancellationToken: Ct
        );
        var read = await _applications.Versions.GetCurrentAsync(
            created.EntityId,
            cancellationToken: Ct
        );
        var stored = await _raw.Find(new BsonDocument("_id", created.Id)).SingleAsync(Ct);
        stored["details"]["_t"].AsString.Should().Be(stored["applicationType"].AsString);
        return (read!, stored);
    }

    [Fact]
    public async Task OrsChange_RoundTripsWithItsKindsDeclarationAndSite()
    {
        var siteId = ObjectId.GenerateNewId();
        var (read, stored) = await RoundTripAsync(
            DomainModelBuilders.OrsChangeDetails(siteId, interimSiteNumbers: "001")
        );

        read.ApplicationType.Should().Be(ApplicationType.OrsChange);
        var details = read.Details.Should().BeOfType<OrsChangeDetails>().Subject;
        details.OverseasSiteId.Should().Be(siteId);
        details
            .ChangeKinds.Should()
            .Equal(OrsChangeKind.EditSite, OrsChangeKind.ChangeInterimSites);
        details.Declaration!.DeclaredBy.FullName.Should().Be("A Declarer");
        details.Site.OverseasSiteId.Should().Be(siteId);
        details.Site.InterimSites.Should().ContainSingle(i => i.SiteNumber == "001");
        stored["applicationType"].AsString.Should().Be("OrsChange");
        stored["details"]["changeKinds"][0].AsString.Should().Be("EditSite");
    }

    [Fact]
    public async Task TonnageChange_RoundTripsWithItsBandsStoredAsReExValues()
    {
        var (read, stored) = await RoundTripAsync(DomainModelBuilders.TonnageChangeDetails());

        read.ApplicationType.Should().Be(ApplicationType.TonnageChange);
        var details = read.Details.Should().BeOfType<TonnageChangeDetails>().Subject;
        details.FromBand.Should().Be(TonnageBand.UpTo5000);
        details.ToBand.Should().Be(TonnageBand.UpTo10000);
        stored["details"]["fromBand"].AsString.Should().Be("up_to_5000");
        stored["details"]["toBand"].AsString.Should().Be("up_to_10000");
    }

    [Fact]
    public async Task NewRegistration_RoundTripsWithItsNamedSectionStatuses()
    {
        var (read, stored) = await RoundTripAsync(DomainModelBuilders.NewRegistrationDetails());

        read.ApplicationType.Should().Be(ApplicationType.NewRegistration);
        read.Details.Should()
            .BeOfType<NewRegistrationDetails>()
            .Which.Sections.Should()
            .ContainSingle(s => s.Name == "wasteDetails" && s.Status == SectionStatus.InProgress);
        stored["details"]["sections"][0]["status"].AsString.Should().Be("Completed");
    }

    [Fact]
    public async Task AnnualCharge_RoundTripsWithItsChargeYear()
    {
        var (read, _) = await RoundTripAsync(DomainModelBuilders.AnnualChargeDetails(2028));

        read.ApplicationType.Should().Be(ApplicationType.AnnualCharge);
        read.Details.Should().BeOfType<AnnualChargeDetails>().Which.ChargeYear.Should().Be(2028);
    }
}
