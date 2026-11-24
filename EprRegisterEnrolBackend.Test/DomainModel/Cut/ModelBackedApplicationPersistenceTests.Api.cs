using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.DomainModel;
using EprRegisterEnrolBackend.DomainModel.Cut;
using EprRegisterEnrolBackend.DomainModel.Migration;
using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.ReadModel;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Utils.Mongo;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// The service-client accreditation API's queries (B13): after the cut they find granted accreditations in the model,
/// which also ends the API's lookup through legacy (decision A of RA-705, accepted as phase 6 work).
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private const string UnservableOrganisationId = "6512a0f0c0ffee00000000c1";
    private const int FirstYear = 2026;

    // The lookup data, plus a second organisation's approved 2027 accreditation and an approved application the API
    // can't serve: no organisation number, no registration number and no accreditation number.
    private async Task<LookupData> ApiDataAsync()
    {
        var data = await LookupDataAsync();
        await MigratedAsync(
            Variant(
                Day1.AddDays(5),
                a =>
                {
                    a.RegistrationId = OtherRegistrationId;
                    a.RegistrationReference = "R27EX0000001P";
                    a.OrganisationName = "Other Recycling Ltd";
                    a.ApplicationStatus = ApplicationStatus.Approved;
                    a.AccreditationReference = "A27EX0000001P";
                    a.DateLastEdited = Day1.AddDays(6);
                    a.OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites = [Site(5, "005")],
                    };
                },
                OtherOrganisationId
            )
        );
        await MigratedAsync(
            Variant(
                Day1.AddDays(6),
                a =>
                {
                    a.RegistrationId = "6512a0f0c0ffee00000000c2";
                    a.RegistrationReference = null;
                    a.OrganisationName = "Unnumbered Ltd";
                    a.OrgId = null;
                    a.ApplicationStatus = ApplicationStatus.Approved;
                    a.AccreditationReference = null;
                    a.OverseasSites = null;
                },
                UnservableOrganisationId
            )
        );
        return data;
    }

    [Theory]
    [InlineData(500001, "R27EX1234567P", 2026)]
    [InlineData(500001, "R27EX1234567P", 2027)]
    [InlineData(500027, "R27EX0000001P", 2027)]
    [InlineData(500027, "R27EX1234567P", 2027)]
    public async Task GetGrantedAccreditation_FindsTheSameApplication(
        int orgId,
        string registration,
        int year
    )
    {
        await ApiDataAsync();

        var legacy = await _legacy.GetGrantedAccreditationAsync(
            orgId,
            registration,
            year,
            FirstYear
        );
        var model = await _model.GetGrantedAccreditationAsync(orgId, registration, year, FirstYear);

        Assert.Equal(legacy?.Id, model?.Id);
    }

    [Fact]
    public async Task GetGrantedAccreditation_BeforeTheFirstYear_IsNull()
    {
        await ApiDataAsync();

        Assert.Null(await _model.GetGrantedAccreditationAsync(500001, "R27EX1234567P", 2026, 2027));
    }

    public static TheoryData<int?, int?, string?, int, int> SearchCases =>
        new()
        {
            { null, null, null, 0, 10 },
            { 2026, null, null, 0, 10 },
            { 2027, null, null, 0, 10 },
            { null, 500027, null, 0, 10 },
            { null, null, "R27EX1234567P", 0, 10 },
            // An empty registration number still filters, as legacy does, so finds nothing.
            { null, null, "", 0, 10 },
            { null, null, null, 1, 1 },
            { null, null, null, 5, 10 },
        };

    [Theory]
    [MemberData(nameof(SearchCases))]
    public async Task SearchGrantedAccreditations_FindsTheSamePageAndTotal(
        int? year,
        int? organisationNumber,
        string? registrationNumber,
        int skip,
        int limit
    )
    {
        await ApiDataAsync();
        var filter = new GrantedAccreditationFilter(year, organisationNumber, registrationNumber);

        var (legacyItems, legacyTotal) = await _legacy.SearchGrantedAccreditationsAsync(
            filter,
            FirstYear,
            skip,
            limit
        );
        var (modelItems, modelTotal) = await _model.SearchGrantedAccreditationsAsync(
            filter,
            FirstYear,
            skip,
            limit
        );

        Assert.Equal(legacyTotal, modelTotal);
        Assert.Equal(Ids(legacyItems), Ids(modelItems));
    }

    // RA-702: every accreditation served is approved, so a search for any other status finds none.
    [Theory]
    [InlineData("approved")]
    [InlineData("suspended")]
    [InlineData("cancelled")]
    public async Task SearchGrantedAccreditations_ByStatus_FindsTheSamePageAndTotal(string status)
    {
        await ApiDataAsync();
        var filter = new GrantedAccreditationFilter(Status: status);

        var (legacyItems, legacyTotal) = await _legacy.SearchGrantedAccreditationsAsync(
            filter,
            FirstYear,
            0,
            10
        );
        var (modelItems, modelTotal) = await _model.SearchGrantedAccreditationsAsync(
            filter,
            FirstYear,
            0,
            10
        );

        Assert.Equal(status == "approved", legacyTotal > 0);
        Assert.Equal(legacyTotal, modelTotal);
        Assert.Equal(Ids(legacyItems), Ids(modelItems));
    }

    [Theory]
    [InlineData(500001, "R27EX1234567P", FirstYear, 3)]
    [InlineData(500001, "R27EX1234567P", 2027, 2)]
    [InlineData(500027, "R27EX0000001P", FirstYear, 2)]
    [InlineData(500027, "R27EX1234567P", FirstYear, 0)]
    [InlineData(500099, "R27EX1234567P", FirstYear, 0)]
    public async Task GetByOrgIdAndRegistrationNumber_FindsTheSameApplications_OfEveryStatus(
        int orgId,
        string registration,
        int firstYear,
        int expected
    )
    {
        await ApiDataAsync();

        var legacy = await _legacy.GetByOrgIdAndRegistrationNumberAsync(orgId, registration, firstYear);
        var model = await _model.GetByOrgIdAndRegistrationNumberAsync(orgId, registration, firstYear);

        Assert.Equal(expected, legacy.Count);
        Assert.Equal(legacy.Select(a => a.Id), model.Select(a => a.Id));
    }

    // RA-702's load protection: the API passes a time limit, and the model's lookup query asks the server for it, as
    // legacy's does. The server stopping a query past it is what answers 503 query_timeout.
    [Fact]
    public async Task SearchAndGetGranted_SendTheTimeLimitWithTheLookupQuery()
    {
        await ApiDataAsync();
        var commands = new List<BsonDocument>();
        var model = ModelOver(
            TestMongoDbClientFactory.WatchingCommands(_connectionString, _databaseName, commands)
        );

        await model.SearchGrantedAccreditationsAsync(
            new GrantedAccreditationFilter(),
            FirstYear,
            0,
            10,
            TimeSpan.FromSeconds(7)
        );
        await model.GetGrantedAccreditationAsync(
            500027,
            "R27EX0000001P",
            2027,
            FirstYear,
            TimeSpan.FromSeconds(7)
        );

        var lookups = commands
            .Where(c =>
                c.TryGetValue("find", out var collection)
                && collection.AsString == DomainModelCollections.ApplicationVersions
            )
            .ToList();
        Assert.Equal(
            2,
            lookups.Count(c => c.TryGetValue("maxTimeMS", out var ms) && ms.ToInt64() == 7000)
        );
    }

    // The model-backed persistence over another client on the same database; reads only, so nothing flushes.
    private ModelBackedApplicationPersistence ModelOver(IMongoDbClientFactory factory)
    {
        var clock = new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc));
        var logs = NullLoggerFactory.Instance;
        var stores = new ApplicationGroupStores(
            new OrganisationPersistence(factory, logs),
            new RegistrationPersistence(factory, logs, clock),
            new OverseasSitePersistence(factory, logs, clock),
            new ApplicationVersionPersistence(factory, logs, clock),
            new AccreditationPersistence(factory, logs, clock),
            new MigrationConflictPersistence(factory, logs)
        );
        return new ModelBackedApplicationPersistence(
            factory,
            stores,
            new ApplicationReadModel(stores),
            new CutStagingPersistence(factory, logs),
            () => throw new InvalidOperationException("A read flushes nothing."),
            NullLogger<ModelBackedApplicationPersistence>.Instance
        );
    }

    [Fact]
    public async Task CountUnservableApproved_CountsTheSame()
    {
        await ApiDataAsync();

        var legacy = await _legacy.CountUnservableApprovedAsync(FirstYear, Ct);
        var model = await _model.CountUnservableApprovedAsync(FirstYear, Ct);

        Assert.Equal(new UnservableApprovedCounts(1, 1, 1), legacy);
        Assert.Equal(legacy, model);
    }
}
