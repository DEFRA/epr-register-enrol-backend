using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.DomainModel.Persistence;

/// <summary>
/// The typed helpers every application type is read and written through, and the open-site query that has to see
/// every type: a site held by one open application is held against all the others, whichever way round.
/// </summary>
public sealed class ApplicationVersionsTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly TestClock _clock = new(new DateTime(2027, 3, 1, 9, 0, 0, DateTimeKind.Utc));
    private readonly ApplicationVersionPersistence _applications;
    private readonly ObjectId _registrationId = ObjectId.GenerateNewId();

    public ApplicationVersionsTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("appversions");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _applications = new ApplicationVersionPersistence(
            _factory,
            NullLoggerFactory.Instance,
            _clock
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static ChangeInfo Change() => new() { Reason = ChangeReason.ApplicationEdited };

    private Task<ApplicationEntity> CreateAsync<TDetails>(
        TDetails details,
        ApplicationStatus status = ApplicationStatus.Submitted,
        ObjectId? registrationId = null
    )
        where TDetails : ApplicationDetails =>
        _applications.CreateAsync<TDetails>(
            DomainModelBuilders.Application(registrationId ?? _registrationId, details, status),
            Change(),
            cancellationToken: Ct
        );

    // --- create ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_DetailsOfTheNamedType_StoresVersionOne()
    {
        var created = await CreateAsync(DomainModelBuilders.TonnageChangeDetails());

        created.Version.Should().Be(1);
        created.ApplicationType.Should().Be(ApplicationType.TonnageChange);
    }

    [Fact]
    public async Task Create_DetailsOfAnotherType_IsRefusedAndWritesNothing()
    {
        var application = DomainModelBuilders.Application(
            _registrationId,
            DomainModelBuilders.AnnualChargeDetails()
        );

        var act = () =>
            _applications.CreateAsync<TonnageChangeDetails>(
                application,
                Change(),
                cancellationToken: Ct
            );

        await act.Should().ThrowAsync<ArgumentException>();
        (await _applications.ListCurrentAsync(_registrationId, cancellationToken: Ct))
            .Should()
            .BeEmpty();
    }

    // --- read -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetCurrent_OfTheStoredType_ReturnsIt()
    {
        var created = await CreateAsync(DomainModelBuilders.AnnualChargeDetails(2028));

        var read = await _applications.GetCurrentAsync<AnnualChargeDetails>(
            created.EntityId,
            cancellationToken: Ct
        );

        ((AnnualChargeDetails)read!.Details).ChargeYear.Should().Be(2028);
    }

    [Fact]
    public async Task GetCurrent_OfAnotherType_ReturnsNull()
    {
        var created = await CreateAsync(DomainModelBuilders.AnnualChargeDetails());

        var read = await _applications.GetCurrentAsync<TonnageChangeDetails>(
            created.EntityId,
            cancellationToken: Ct
        );

        read.Should().BeNull();
    }

    [Fact]
    public async Task GetCurrent_NoSuchApplication_ReturnsNull()
    {
        var read = await _applications.GetCurrentAsync<AccreditationDetails>(
            ObjectId.GenerateNewId(),
            cancellationToken: Ct
        );

        read.Should().BeNull();
    }

    // --- update ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_SameType_WritesTheNextVersion()
    {
        var created = await CreateAsync(DomainModelBuilders.TonnageChangeDetails());
        ((TonnageChangeDetails)created.Details).ToBand = TonnageBand.Over10000;

        var result = await _applications.UpdateAsync<TonnageChangeDetails>(
            created,
            created.Version,
            Change(),
            cancellationToken: Ct
        );

        result.Changed.Should().BeTrue();
        result.Entity.Version.Should().Be(2);
        ((TonnageChangeDetails)result.Entity.Details).ToBand.Should().Be(TonnageBand.Over10000);
    }

    [Fact]
    public async Task Update_ToAnotherType_IsRefusedAndWritesNothing()
    {
        var created = await CreateAsync(DomainModelBuilders.TonnageChangeDetails());
        var retyped = DomainModelBuilders.Application(
            _registrationId,
            DomainModelBuilders.AnnualChargeDetails()
        );
        retyped.EntityId = created.EntityId;

        var act = () =>
            _applications.UpdateAsync<AnnualChargeDetails>(
                retyped,
                created.Version,
                Change(),
                cancellationToken: Ct
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
        var versions = await _applications.Versions.GetVersionsAsync(
            created.EntityId,
            cancellationToken: Ct
        );
        versions
            .Should()
            .ContainSingle()
            .Which.ApplicationType.Should()
            .Be(ApplicationType.TonnageChange);
    }

    [Fact]
    public async Task Update_DetailsNotOfTheNamedType_IsRefused()
    {
        var created = await CreateAsync(DomainModelBuilders.TonnageChangeDetails());

        var act = () =>
            _applications.UpdateAsync<AnnualChargeDetails>(
                created,
                created.Version,
                Change(),
                cancellationToken: Ct
            );

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // --- list -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListCurrent_ReturnsTheRegistrationsCurrentVersionsOnly()
    {
        var tonnage = await CreateAsync(DomainModelBuilders.TonnageChangeDetails());
        ((TonnageChangeDetails)tonnage.Details).ToBand = TonnageBand.Over10000;
        await _applications.UpdateAsync<TonnageChangeDetails>(
            tonnage,
            tonnage.Version,
            Change(),
            cancellationToken: Ct
        );
        var charge = await CreateAsync(DomainModelBuilders.AnnualChargeDetails());
        await CreateAsync(
            DomainModelBuilders.AnnualChargeDetails(),
            registrationId: ObjectId.GenerateNewId()
        );

        var listed = await _applications.ListCurrentAsync(_registrationId, cancellationToken: Ct);

        listed
            .Select(a => (a.EntityId, a.Version))
            .Should()
            .BeEquivalentTo([(tonnage.EntityId, 2L), (charge.EntityId, 1L)]);
    }

    [Fact]
    public async Task ListCurrent_FilteredByType_ReturnsOnlyThatType()
    {
        var orsChange = await CreateAsync(
            DomainModelBuilders.OrsChangeDetails(ObjectId.GenerateNewId())
        );
        await CreateAsync(DomainModelBuilders.TonnageChangeDetails());

        var listed = await _applications.ListCurrentAsync(
            _registrationId,
            ApplicationType.OrsChange,
            cancellationToken: Ct
        );

        listed.Should().ContainSingle().Which.EntityId.Should().Be(orsChange.EntityId);
    }

    [Fact]
    public async Task ListCurrent_ByRegistrationAndType_UsesTheRegistrationAndTypeIndex()
    {
        await CreateAsync(DomainModelBuilders.TonnageChangeDetails());

        var explain = await _factory
            .GetClient()
            .GetDatabase(_databaseName)
            .RunCommandAsync<BsonDocument>(
                new BsonDocument(
                    "explain",
                    new BsonDocument
                    {
                        { "find", DomainModelCollections.ApplicationVersions },
                        {
                            "filter",
                            new BsonDocument
                            {
                                { "registrationId", _registrationId },
                                { "applicationType", "TonnageChange" },
                                { "isCurrent", true },
                            }
                        },
                    }
                ),
                cancellationToken: Ct
            );

        IndexNames(explain["queryPlanner"]["winningPlan"])
            .Should()
            .Contain("registrationId_applicationType_current");
    }

    private static IEnumerable<string> IndexNames(BsonValue stage) =>
        stage switch
        {
            BsonDocument doc => doc.Elements.SelectMany(e =>
                e.Name == "indexName" ? [e.Value.AsString] : IndexNames(e.Value)
            ),
            BsonArray items => items.SelectMany(IndexNames),
            _ => [],
        };

    // --- sites held by other open applications --------------------------------------------------------------------

    private async Task<ApplicationEntity> ReapplicationHoldingAsync(
        ObjectId overseasSiteId,
        string interimSiteNumber
    )
    {
        var details = DomainModelBuilders.AccreditationDetails();
        var entry = details.ApplicationOverseasSites.Single();
        entry.OverseasSiteId = overseasSiteId;
        entry.InterimSites.Single().SiteNumber = interimSiteNumber;
        return await CreateAsync(details, ApplicationStatus.Started);
    }

    [Fact]
    public async Task SitesHeld_AnOpenOrsChange_HoldsItsSiteAndInterimSitesAgainstAReapplication()
    {
        var siteId = ObjectId.GenerateNewId();
        var reapplication = await ReapplicationHoldingAsync(ObjectId.GenerateNewId(), "009");
        await CreateAsync(DomainModelBuilders.OrsChangeDetails(siteId, interimSiteNumbers: "001"));

        var held = await _applications.SitesHeldByOtherOpenApplicationsAsync(
            _registrationId,
            reapplication.EntityId,
            cancellationToken: Ct
        );

        held.Sites.Should().Contain(siteId);
        held.Interims.Should().Contain((siteId, "001"));
    }

    [Fact]
    public async Task SitesHeld_AnOpenReapplication_HoldsItsSitesAgainstAnOrsChange()
    {
        var siteId = ObjectId.GenerateNewId();
        await ReapplicationHoldingAsync(siteId, "001");
        var orsChange = await CreateAsync(
            DomainModelBuilders.OrsChangeDetails(ObjectId.GenerateNewId())
        );

        var held = await _applications.SitesHeldByOtherOpenApplicationsAsync(
            _registrationId,
            orsChange.EntityId,
            cancellationToken: Ct
        );

        held.Sites.Should().Contain(siteId);
        held.Interims.Should().Contain((siteId, "001"));
    }

    [Fact]
    public async Task SitesHeld_ClosedApplicationsAndOtherRegistrations_HoldNothing()
    {
        var self = await ReapplicationHoldingAsync(ObjectId.GenerateNewId(), "009");
        await CreateAsync(
            DomainModelBuilders.OrsChangeDetails(ObjectId.GenerateNewId()),
            ApplicationStatus.Withdrawn
        );
        await CreateAsync(
            DomainModelBuilders.OrsChangeDetails(ObjectId.GenerateNewId()),
            registrationId: ObjectId.GenerateNewId()
        );

        var held = await _applications.SitesHeldByOtherOpenApplicationsAsync(
            _registrationId,
            self.EntityId,
            cancellationToken: Ct
        );

        held.Sites.Should().BeEmpty();
        held.Interims.Should().BeEmpty();
    }

    [Fact]
    public async Task SitesHeld_TypesThatTouchNoOverseasSite_HoldNothing()
    {
        var self = await ReapplicationHoldingAsync(ObjectId.GenerateNewId(), "009");
        await CreateAsync(DomainModelBuilders.TonnageChangeDetails());
        await CreateAsync(DomainModelBuilders.NewRegistrationDetails());
        await CreateAsync(DomainModelBuilders.AnnualChargeDetails());

        var held = await _applications.SitesHeldByOtherOpenApplicationsAsync(
            _registrationId,
            self.EntityId,
            cancellationToken: Ct
        );

        held.Sites.Should().BeEmpty();
    }
}
