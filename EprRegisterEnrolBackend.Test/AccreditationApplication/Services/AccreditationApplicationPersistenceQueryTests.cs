using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Services;

/// <summary>
/// RA-516: proves GetLiveByRegistrationAsync and GetOrsIdsByRegistrationAsync actually filter,
/// sort, and limit server-side against a real mongod, rather than fetching every application for
/// the organisation and processing the result in memory the way the endpoints used to.
/// </summary>
public sealed class AccreditationApplicationPersistenceQueryTests : IDisposable
{
    private readonly string _databaseName;
    private readonly TestMongoDbClientFactory _factory;
    private readonly AccreditationApplicationPersistence _sut;

    public AccreditationApplicationPersistenceQueryTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("accreditation_queries");
        _factory = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _sut = new AccreditationApplicationPersistence(_factory, NullLoggerFactory.Instance);
    }

    public void Dispose() => _factory.GetClient().DropDatabase(_databaseName);

    private static AccreditationApplicationModel BuildApplication(
        string organisationId = "org-1",
        string? registrationId = "reg-1",
        MaterialType materialType = MaterialType.Steel,
        int year = 2026,
        ApplicationStatus status = ApplicationStatus.Saved,
        DateTime? createdAt = null
    ) =>
        new()
        {
            OrganisationId = organisationId,
            RegistrationId = registrationId,
            MaterialType = materialType,
            Year = year,
            ApplicationStatus = status,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };

    [Fact]
    public async Task GetLiveByRegistrationAsync_MultipleMatches_ReturnsNewestByCreatedAt()
    {
        var older = BuildApplication(createdAt: DateTime.UtcNow.AddDays(-2));
        var newer = BuildApplication(createdAt: DateTime.UtcNow.AddDays(-1));
        await _sut.CreateAsync(older);
        await _sut.CreateAsync(newer);

        var result = await _sut.GetLiveByRegistrationAsync(
            "org-1",
            "reg-1",
            MaterialType.Steel,
            2026
        );

        result.Should().NotBeNull();
        result!.Id.Should().Be(newer.Id);
    }

    [Fact]
    public async Task GetLiveByRegistrationAsync_OnlyMatchIsWithdrawn_ReturnsNull()
    {
        await _sut.CreateAsync(BuildApplication(status: ApplicationStatus.Withdrawn));

        var result = await _sut.GetLiveByRegistrationAsync(
            "org-1",
            "reg-1",
            MaterialType.Steel,
            2026
        );

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLiveByRegistrationAsync_NoMatchForKey_ReturnsNull()
    {
        await _sut.CreateAsync(BuildApplication(registrationId: "some-other-reg"));

        var result = await _sut.GetLiveByRegistrationAsync(
            "org-1",
            "reg-1",
            MaterialType.Steel,
            2026
        );

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLiveByRegistrationAsync_NewestIsWithdrawn_ReturnsNewestNonWithdrawn()
    {
        var live = BuildApplication(createdAt: DateTime.UtcNow.AddDays(-2));
        var withdrawn = BuildApplication(
            status: ApplicationStatus.Withdrawn,
            createdAt: DateTime.UtcNow.AddDays(-1)
        );
        await _sut.CreateAsync(live);
        await _sut.CreateAsync(withdrawn);

        var result = await _sut.GetLiveByRegistrationAsync(
            "org-1",
            "reg-1",
            MaterialType.Steel,
            2026
        );

        result.Should().NotBeNull();
        result!.Id.Should().Be(live.Id);
    }

    [Fact]
    public async Task GetOrsIdsByRegistrationAsync_FlattensSitesAcrossApplicationsSharingRegistrationId()
    {
        var withSites = BuildApplication();
        withSites.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "001",
                    SiteName = "Site A",
                },
                new OverseasSiteModel
                {
                    SiteId = 2,
                    OrsId = null,
                    SiteName = "Site B",
                },
            ],
        };
        var secondYear = BuildApplication(year: 2025);
        secondYear.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    OrsId = "002",
                    SiteName = "Site C",
                },
            ],
        };
        await _sut.CreateAsync(withSites);
        await _sut.CreateAsync(secondYear);

        var result = await _sut.GetOrsIdsByRegistrationAsync("reg-1");

        result.Should().BeEquivalentTo(["001", "002"]);
    }

    [Fact]
    public async Task GetOrsIdsByRegistrationAsync_NoApplicationsForRegistration_ReturnsEmpty()
    {
        await _sut.CreateAsync(BuildApplication(registrationId: "some-other-reg"));

        var result = await _sut.GetOrsIdsByRegistrationAsync("reg-1");

        result.Should().BeEmpty();
    }
}
