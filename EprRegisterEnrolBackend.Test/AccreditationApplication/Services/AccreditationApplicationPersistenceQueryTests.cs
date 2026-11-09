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

    // ── RA-603: interim site numbers ─────────────────────────────────────────
    //
    // These run against a real mongod on purpose. The fake persistence cannot prove the nested
    // filter below actually translates — a site number lives on an interim site inside an overseas
    // site, so the absence guard is an ElemMatch over the ORS list whose predicate is itself an Any
    // over that ORS's interim sites. A driver that failed to translate that would throw here and
    // pass every fake-backed test.

    private static OverseasSiteModel SiteWithInterim(
        int siteId,
        params InterimSiteModel[] interimSites
    ) =>
        new()
        {
            SiteId = siteId,
            SiteName = "ORS " + siteId,
            InterimSites = [.. interimSites],
        };

    private static InterimSiteModel Interim(
        int siteId,
        string siteNumber,
        DateTime? removedAt = null
    ) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            Country = "France",
            SiteName = "Interim " + siteId,
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Marie Curie",
            ContactEmail = "marie@example.com",
            ContactPhone = "0033111222333",
            RemovedAt = removedAt,
        };

    [Fact]
    public async Task GetInterimSiteNumbersByRegistrationAsync_FlattensAcrossApplicationsAndOverseasSites()
    {
        var current = BuildApplication();
        current.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [SiteWithInterim(1, Interim(2, "001")), SiteWithInterim(3, Interim(4, "002"))],
        };
        var priorYear = BuildApplication(year: 2025);
        priorYear.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [SiteWithInterim(1, Interim(2, "003"))],
        };
        await _sut.CreateAsync(current);
        await _sut.CreateAsync(priorYear);

        var result = await _sut.GetInterimSiteNumbersByRegistrationAsync("reg-1");

        result.Should().BeEquivalentTo(["001", "002", "003"]);
    }

    // AC05 keeps a withdrawn record permanently, so its number stays claimed.
    [Fact]
    public async Task GetInterimSiteNumbersByRegistrationAsync_IncludesWithdrawnInterimSites()
    {
        var application = BuildApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [SiteWithInterim(1, Interim(2, "001", removedAt: DateTime.UtcNow))],
        };
        await _sut.CreateAsync(application);

        var result = await _sut.GetInterimSiteNumbersByRegistrationAsync("reg-1");

        result.Should().BeEquivalentTo(["001"]);
    }

    // A document written before RA-603 carries only the singular mirror and no list.
    [Fact]
    public async Task GetInterimSiteNumbersByRegistrationAsync_ReadsTheLegacySingularField()
    {
        var application = BuildApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "ORS 1",
                    InterimSite = Interim(2, "007"),
                },
            ],
        };
        await _sut.CreateAsync(application);

        var result = await _sut.GetInterimSiteNumbersByRegistrationAsync("reg-1");

        result.Should().BeEquivalentTo(["007"]);
    }

    [Fact]
    public async Task GetInterimSiteNumbersByRegistrationAsync_IgnoresOtherRegistrations()
    {
        var other = BuildApplication(registrationId: "some-other-reg");
        other.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [SiteWithInterim(1, Interim(2, "500"))],
        };
        await _sut.CreateAsync(other);

        var result = await _sut.GetInterimSiteNumbersByRegistrationAsync("reg-1");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateIfInterimSiteNumberAbsentAsync_NumberNotYetUsed_Persists()
    {
        var application = BuildApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [SiteWithInterim(1)],
        };
        await _sut.CreateAsync(application);

        application.OverseasSites.Sites[0].InterimSites.Add(Interim(2, "001"));
        var result = await _sut.UpdateIfInterimSiteNumberAbsentAsync(application, "001");

        result.Should().NotBeNull();
        var stored = await _sut.GetByIdAsync("org-1", application.Id!.Value.ToString());
        stored!
            .OverseasSites!.Sites[0]
            .InterimSites.Select(i => i.SiteNumber)
            .Should()
            .BeEquivalentTo(["001"]);
    }

    // The concurrency guard: a number another writer already claimed is refused rather than
    // duplicated, which is what makes AddInterimSite's retry loop necessary and safe.
    [Fact]
    public async Task UpdateIfInterimSiteNumberAbsentAsync_NumberAlreadyClaimed_RefusesTheWrite()
    {
        var application = BuildApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites = [SiteWithInterim(1, Interim(2, "001"))],
        };
        await _sut.CreateAsync(application);

        var result = await _sut.UpdateIfInterimSiteNumberAbsentAsync(application, "001");

        result.Should().BeNull();
    }

    // RA-603 review (Aysha): the number scope reads the legacy singular mirror (see
    // GetInterimSiteNumbersByRegistrationAsync_ReadsTheLegacySingularField), so the write guard
    // has to as well - otherwise a number held only in an un-normalised mirror is invisible to it.
    // The Version matches, so a refusal here can only come from the number guard.
    [Fact]
    public async Task UpdateIfInterimSiteNumberAbsentAsync_NumberHeldOnlyByALegacyMirror_RefusesTheWrite()
    {
        var application = BuildApplication();
        application.OverseasSites = new AccreditationApplicationOverseasSites
        {
            Sites =
            [
                new OverseasSiteModel
                {
                    SiteId = 1,
                    SiteName = "ORS 1",
                    InterimSite = Interim(2, "005"),
                },
            ],
        };
        await _sut.CreateAsync(application);

        var result = await _sut.UpdateIfInterimSiteNumberAbsentAsync(application, "005");

        result.Should().BeNull();
    }

    // ── RA-690: latest previous approved accreditation ───────────────────────
    //
    // Against a real mongod so the filter (including the null glass process matching a non-glass
    // document) and the year-then-edited ordering are proven to run server-side.

    private async Task<AccreditationApplicationModel> CreateApproved(
        int year,
        string? accreditationReference,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var application = BuildApplication(year: year, status: ApplicationStatus.Approved);
        application.AccreditationReference = accreditationReference;
        configure?.Invoke(application);
        await _sut.CreateAsync(application);
        return application;
    }

    private Task<AccreditationApplicationModel?> LatestPreviousBefore(
        int year,
        MaterialType materialType = MaterialType.Steel,
        GlassRecyclingProcess? glassRecyclingProcess = null
    ) =>
        _sut.GetLatestPreviousAccreditationAsync(
            "org-1",
            "reg-1",
            materialType,
            glassRecyclingProcess,
            year
        );

    [Fact]
    public async Task GetLatestPreviousAccreditationAsync_NAndNPlus1Approved_ReturnsNPlus1()
    {
        await CreateApproved(2026, "A26ER0001230042ST");
        var latest = await CreateApproved(2027, "A27ER0001230042ST");

        var result = await LatestPreviousBefore(2028);

        result!.Id.Should().Be(latest.Id);
    }

    [Fact]
    public async Task GetLatestPreviousAccreditationAsync_OnlyNApproved_ReturnsNAcrossTheGap()
    {
        var only = await CreateApproved(2026, "A26ER0001230042ST");

        var result = await LatestPreviousBefore(2028);

        result!.Id.Should().Be(only.Id);
    }

    [Fact]
    public async Task GetLatestPreviousAccreditationAsync_SameYearTie_ReturnsMostRecentlyEdited()
    {
        // The older one is inserted first, so a missing tie-break can't pass by returning the
        // first-inserted document.
        await CreateApproved(
            2026,
            "A26ER0001230042ST",
            a => a.DateLastEdited = DateTime.UtcNow.AddDays(-2)
        );
        var newer = await CreateApproved(
            2026,
            "A26ER0001230077ST",
            a => a.DateLastEdited = DateTime.UtcNow.AddDays(-1)
        );

        var result = await LatestPreviousBefore(2027);

        result!.Id.Should().Be(newer.Id);
    }

    [Fact]
    public async Task GetLatestPreviousAccreditationAsync_NewerNonMatchesPresent_ReturnsTheOlderMatch()
    {
        const string Reference = "A26ER0001230042ST";
        var match = await CreateApproved(2025, "A25ER0001230042ST");
        await CreateApproved(
            2026,
            Reference,
            a => a.ApplicationStatus = ApplicationStatus.Withdrawn
        );
        await CreateApproved(
            2026,
            Reference,
            a => a.ApplicationStatus = ApplicationStatus.Rejected
        );
        await CreateApproved(2026, null);
        await CreateApproved(2026, string.Empty);
        await CreateApproved(2026, Reference, a => a.OrganisationId = "org-other");
        await CreateApproved(2026, Reference, a => a.RegistrationId = "reg-other");
        await CreateApproved(
            2026,
            "A26ER0001230042AL",
            a => a.MaterialType = MaterialType.Aluminium
        );
        // Same year as the one applied for, so not "previous".
        await CreateApproved(2027, "A27ER0001230042ST");

        var result = await LatestPreviousBefore(2027);

        result!.Id.Should().Be(match.Id);
    }

    [Fact]
    public async Task GetLatestPreviousAccreditationAsync_NothingEarlier_ReturnsNull()
    {
        await CreateApproved(2026, "A26ER0001230042ST");

        var result = await LatestPreviousBefore(2026);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestPreviousAccreditationAsync_GlassApplications_ReturnsOnlyTheSameGlassProcess()
    {
        void Glass(AccreditationApplicationModel a, GlassRecyclingProcess process)
        {
            a.MaterialType = MaterialType.Glass;
            a.GlassRecyclingProcess = process;
        }
        var remelt = await CreateApproved(
            2026,
            "A26ER0001230042GR",
            a => Glass(a, GlassRecyclingProcess.Remelt)
        );
        var other = await CreateApproved(
            2026,
            "A26ER0001230042GO",
            a => Glass(a, GlassRecyclingProcess.Other)
        );

        (await LatestPreviousBefore(2027, MaterialType.Glass, GlassRecyclingProcess.Remelt))!
            .Id.Should()
            .Be(remelt.Id);
        (await LatestPreviousBefore(2027, MaterialType.Glass, GlassRecyclingProcess.Other))!
            .Id.Should()
            .Be(other.Id);
        (await LatestPreviousBefore(2027, MaterialType.Glass)).Should().BeNull();
    }

    private async Task<AccreditationApplicationModel> CreateWithReference(
        string? accreditationReference,
        string organisationId = "org-1",
        ApplicationStatus status = ApplicationStatus.Submitted
    )
    {
        var application = BuildApplication(organisationId: organisationId, status: status);
        application.AccreditationReference = accreditationReference;
        await _sut.CreateAsync(application);
        return application;
    }

    private Task<IReadOnlyList<AccreditationReferenceHolder>> OtherHolders(
        AccreditationApplicationModel applicant,
        string accreditationReference = "A26ER0001230042ST"
    ) =>
        _sut.GetOtherAccreditationReferenceHoldersAsync(
            accreditationReference,
            applicant.Id!.Value
        );

    // RA-690: a withdrawn holder keeps its reference for audit, so it is still returned (and
    // logged), but it never blocks a restart from being issued the same reference.
    [Fact]
    public async Task GetOtherAccreditationReferenceHoldersAsync_WithdrawnHolder_IsReturnedButDoesNotBlock()
    {
        var withdrawn = await CreateWithReference(
            "A26ER0001230042ST",
            organisationId: "org-other",
            status: ApplicationStatus.Withdrawn
        );
        var applicant = await CreateWithReference(null);

        var holders = await OtherHolders(applicant);

        var holder = holders.Should().ContainSingle().Subject;
        holder.Id.Should().Be(withdrawn.Id!.Value);
        holder.ApplicationStatus.Should().Be(ApplicationStatus.Withdrawn);
        holder.BlocksIssuing.Should().BeFalse();
    }

    [Theory]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.AwaitingDecision)]
    public async Task GetOtherAccreditationReferenceHoldersAsync_ApprovedOrOpenHolder_Blocks(
        ApplicationStatus holderStatus
    )
    {
        var live = await CreateWithReference("A26ER0001230042ST", status: holderStatus);
        var applicant = await CreateWithReference(null);

        var holders = await OtherHolders(applicant);

        var holder = holders.Should().ContainSingle().Subject;
        holder.Id.Should().Be(live.Id!.Value);
        holder.ApplicationStatus.Should().Be(holderStatus);
        holder.BlocksIssuing.Should().BeTrue();
    }

    [Fact]
    public async Task GetOtherAccreditationReferenceHoldersAsync_OnlyTheApplicationItselfHoldsIt_ReturnsNone()
    {
        var holder = await CreateWithReference("A26ER0001230042ST");

        (await OtherHolders(holder)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetOtherAccreditationReferenceHoldersAsync_TheApplicationAndAWithdrawnOneHoldIt_ReturnsOnlyTheOther()
    {
        var withdrawn = await CreateWithReference(
            "A26ER0001230042ST",
            status: ApplicationStatus.Withdrawn
        );
        var holder = await CreateWithReference("A26ER0001230042ST");

        (await OtherHolders(holder))
            .Should()
            .ContainSingle()
            .Which.Id.Should()
            .Be(withdrawn.Id!.Value);
    }

    [Fact]
    public async Task GetOtherAccreditationReferenceHoldersAsync_OthersHoldDifferentReferences_ReturnsNone()
    {
        await CreateWithReference("A26ER0001230043ST");
        await CreateWithReference(null);
        var applicant = await CreateWithReference(null);

        (await OtherHolders(applicant)).Should().BeEmpty();
    }
}
