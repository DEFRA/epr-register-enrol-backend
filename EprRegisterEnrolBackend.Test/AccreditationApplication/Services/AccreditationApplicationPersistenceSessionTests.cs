using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

/// <summary>
/// B8: the legacy reads and writes join a dual-write transaction when given its session, against a real
/// single-node replica set.
/// </summary>
public sealed class AccreditationApplicationPersistenceSessionTests : IDisposable
{
    private const string OrganisationId = "org-1";
    private const string RegistrationId = "reg-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _factory;
    private readonly string _databaseName;
    private readonly AccreditationApplicationPersistence _persistence;

    public AccreditationApplicationPersistenceSessionTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("legacysession");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _persistence = new AccreditationApplicationPersistence(
            _factory,
            NullLoggerFactory.Instance
        );
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private async Task<AccreditationApplicationModel> SeedAsync()
    {
        var application = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = OrganisationId,
            RegistrationId = RegistrationId,
            MaterialType = MaterialType.Plastic,
            Year = 2027,
            ApplicationStatus = ApplicationStatus.Saved,
            CaseManagementWorkItemId = Guid.NewGuid(),
        };
        await _persistence.CreateAsync(application);
        return application;
    }

    private Task<AccreditationApplicationModel?> GetAsync(
        AccreditationApplicationModel application,
        IClientSessionHandle? session = null
    ) => _persistence.GetByIdAsync(OrganisationId, application.Id.ToString()!, session);

    [Fact]
    public async Task WritesInASession_AreSeenInsideItOnly_AndRollBackWhenAborted()
    {
        var application = await SeedAsync();
        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        session.StartTransaction();

        var replaced = (await GetAsync(application, session))!;
        replaced.ApplicationStatus = ApplicationStatus.Started;
        Assert.NotNull(await _persistence.UpdateAsync(replaced, session));
        var targeted = await _persistence.UpdateFieldsAsync(
            application.Id!.Value,
            null,
            Builders<AccreditationApplicationModel>.Update.Set(a => a.Year, 2028),
            session,
            Ct
        );
        Assert.NotNull(targeted);

        var inside = (await GetAsync(application, session))!;
        Assert.Equal(ApplicationStatus.Started, inside.ApplicationStatus);
        Assert.Equal(2028, inside.Year);
        Assert.Equal(2, inside.Version);
        Assert.Equal(
            application.Id,
            (
                await _persistence.GetByCaseManagementWorkItemIdAsync(
                    application.CaseManagementWorkItemId!.Value,
                    session
                )
            )!.Id
        );
        Assert.Equal(ApplicationStatus.Saved, (await GetAsync(application))!.ApplicationStatus);

        await session.AbortTransactionAsync(Ct);

        var after = (await GetAsync(application))!;
        Assert.Equal(ApplicationStatus.Saved, after.ApplicationStatus);
        Assert.Equal(2027, after.Year);
        Assert.Equal(0, after.Version);
    }

    [Fact]
    public async Task RegistrationScopedReads_InASession_SeeTheTransactionsOwnSites()
    {
        var application = await SeedAsync();
        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        session.StartTransaction();

        var stored = (await GetAsync(application, session))!;
        stored.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "001",
                    SiteName = "Site One",
                    InterimSites =
                    [
                        new InterimSiteModel
                        {
                            SiteId = 1,
                            SiteNumber = "IS-001",
                            Country = "France",
                            SiteName = "Interim One",
                            AddressLine1 = "1 Rue Example",
                            TownOrCity = "Paris",
                            ContactName = "Contact",
                            ContactEmail = "contact@example.com",
                            ContactPhone = "000",
                        },
                    ],
                },
            ],
        };
        Assert.NotNull(await _persistence.UpdateIfOrsIdAbsentAsync(stored, "001", session));

        Assert.Equal(
            ["001"],
            await _persistence.GetOrsIdsByRegistrationAsync(RegistrationId, session)
        );
        Assert.Equal(
            ["IS-001"],
            await _persistence.GetInterimSiteNumbersByRegistrationAsync(RegistrationId, session)
        );
        Assert.Empty(await _persistence.GetOrsIdsByRegistrationAsync(RegistrationId));
        Assert.Equal(
            application.Id,
            (
                await _persistence.GetLiveByRegistrationAsync(
                    OrganisationId,
                    RegistrationId,
                    MaterialType.Plastic,
                    2027,
                    session
                )
            )!.Id
        );

        await session.AbortTransactionAsync(Ct);
    }
}
