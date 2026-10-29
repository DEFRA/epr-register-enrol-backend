using EprRegisterEnrolBackend.DomainModel.DualWrite;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Versioning;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.DualWrite;

/// <summary>
/// B8d: the interim site numbers the model already uses in a registration, which the legacy allocation must avoid
/// because the migration can renumber interim sites to numbers legacy has never seen.
/// </summary>
public sealed class ModelInterimSiteNumbersTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly OverseasSitePersistence _sites;
    private readonly ModelInterimSiteNumbers _numbers;

    public ModelInterimSiteNumbersTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("interimnumbers");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        _sites = new OverseasSitePersistence(_factory, logs, clock);
        _numbers = new ModelInterimSiteNumbers(
            new ApplicationGroupStores(
                new OrganisationPersistence(_factory, logs),
                new RegistrationPersistence(_factory, logs, clock),
                _sites,
                new ApplicationVersionPersistence(_factory, logs, clock),
                new AccreditationPersistence(_factory, logs, clock),
                new MigrationConflictPersistence(_factory, logs)
            )
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private Task InsertSiteAsync(
        ObjectId registrationId,
        string orsId,
        params InterimSiteEntry[] interims
    ) =>
        _sites.Versions.InsertAsync(
            new OverseasSiteEntity
            {
                RegistrationId = registrationId,
                OrsId = orsId,
                InterimSites = [.. interims],
            },
            new ChangeInfo { Reason = ChangeReason.Migration },
            cancellationToken: Ct
        );

    [Fact]
    public async Task ReturnsEveryCurrentAndPreviousNumberInTheRegistrationOnly()
    {
        var registrationId = ObjectId.GenerateNewId();
        await InsertSiteAsync(
            registrationId,
            "001",
            new InterimSiteEntry { SiteNumber = "007", PreviousSiteNumbers = ["SN-0001"] }
        );
        await InsertSiteAsync(
            registrationId,
            "002",
            new InterimSiteEntry { SiteNumber = "003", Status = InterimSiteStatus.Withdrawn }
        );
        await InsertSiteAsync(
            ObjectId.GenerateNewId(),
            "001",
            new InterimSiteEntry { SiteNumber = "009" }
        );

        var numbers = await _numbers.GetAsync(registrationId, null, Ct);

        Assert.Equal(["003", "007", "SN-0001"], numbers.Order());
    }

    [Fact]
    public async Task NoRegistration_ReturnsNothing()
    {
        Assert.Empty(await _numbers.GetAsync(null, null, Ct));
    }

    [Fact]
    public async Task DualWriteOff_ReturnsNothing()
    {
        Assert.Empty(
            await new NoModelInterimSiteNumbers().GetAsync(ObjectId.GenerateNewId(), null, Ct)
        );
    }
}
