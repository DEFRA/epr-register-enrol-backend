using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.Test.TestSupport;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-697: org 50018's exporter and reprocessor get change requests in every item status and type,
// so the in-year change request pages show content before Case Management can move statuses. Against
// the real store, so the one-draft index and duplicate ids behave as they do in the stack.
public sealed class DevChangeRequestSeedServiceTests : IDisposable
{
    private const int Year = 2026;
    private static readonly string Org = DevExporterOrg.Manage.OrganisationId;
    private static readonly string ExporterApp = DevExporterAccreditationSeedService
        .ApplicationIdFor(DevExporterOrg.Manage, Year)
        .ToString();
    private static readonly string ReprocessorApp = DevExporterAccreditationSeedService
        .ReprocessorApplicationIdFor(DevExporterOrg.Manage, Year)
        .ToString();

    private static readonly TestClock Clock2026 = new(
        new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc)
    );

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestMongoDbClientFactory _mongo;
    private readonly string _databaseName;
    private readonly ChangeRequestPersistence _requests;

    public DevChangeRequestSeedServiceTests(MongoIntegrationFixture fixture)
    {
        _databaseName = MongoIntegrationFixture.NewDatabaseName("dev_change_request_seed");
        _mongo = new TestMongoDbClientFactory(fixture.ConnectionString, _databaseName);
        _requests = new ChangeRequestPersistence(_mongo, NullLoggerFactory.Instance);
    }

    public void Dispose() => _mongo.GetClient().DropDatabase(_databaseName);

    private DevChangeRequestSeedService CreateSut(
        IChangeRequestPersistence? requests = null,
        TimeProvider? clock = null
    ) =>
        new(
            requests ?? _requests,
            clock ?? Clock2026,
            NullLogger<DevChangeRequestSeedService>.Instance
        );

    private async Task<List<AccreditationChangeRequest>> SeedAndList(string applicationId)
    {
        await CreateSut().SeedAsync(Ct);
        return await _requests.ListAsync(Org, applicationId, Ct);
    }

    private static List<ChangeRequestItem> ItemsOf(IEnumerable<AccreditationChangeRequest> requests) =>
        requests.SelectMany(r => r.Items).ToList();

    private static List<OverseasSiteModel> ExporterSites =>
        DevExporterAccreditationSeedService
            .ExporterAccreditation(DevExporterOrg.Manage, Year)
            .OverseasSites!.Sites;

    [Fact]
    public async Task SeedAsync_GivesTheExporterMoreThanAPageOfItemsInEveryStatusAndType()
    {
        var requests = await SeedAndList(ExporterApp);

        var items = ItemsOf(requests);
        items.Count.Should().BeGreaterThan(10);
        items.Select(i => i.Status).Distinct().Should().BeEquivalentTo(Enum.GetValues<ChangeItemStatus>());
        items.Select(i => i.ChangeType)
            .Distinct()
            .Should()
            .BeEquivalentTo(
                AccreditationChangeRequest.AddOverseasSiteChangeType,
                AccreditationChangeRequest.UpdateOverseasSiteChangeType,
                AccreditationChangeRequest.UpgradeTonnageBandChangeType
            );
        items.Select(i => i.ItemId).Should().OnlyHaveUniqueItems();
        requests.Should().OnlyContain(r => r.Year == Year && r.MaterialType == MaterialType.Plastic);
        requests.Should().OnlyContain(r => r.AccreditationReference == "A26EX0500180001PL");
        requests.Should().OnlyContain(r => r.RegistrationId == "aaa000000000000000050018");
    }

    // The one-draft index allows a single InProgress request: Dragon's new ORS accreditation, as
    // RA-492's journey would create. Every other request was submitted to Case Management.
    [Fact]
    public async Task SeedAsync_TheOnlyDraftHoldsTheOnlyInProgressItem_DragonsNewOrsAccreditation()
    {
        var requests = await SeedAndList(ExporterApp);

        var draft = requests.Should().ContainSingle(r => r.Status == ChangeRequestStatus.InProgress).Subject;
        var item = draft.Sites.Should().ContainSingle().Subject;
        (item.OrsId, item.SiteName, item.ChangeType, item.Status)
            .Should()
            .Be(("004", "Dragon Recyclers", AccreditationChangeRequest.AddOverseasSiteChangeType, ChangeItemStatus.InProgress));
        draft.TonnageBandUpgrade.Should().BeNull();
        draft.SubmittedAt.Should().BeNull();
        draft.SubmissionReference.Should().BeNull();
        item.CaseManagementReference.Should().BeNull();

        var submitted = requests.Where(r => r != draft).ToList();
        submitted.Should().OnlyContain(r => r.Status == ChangeRequestStatus.Submitted);
        submitted.Should().OnlyContain(r => r.SubmittedAt != null && r.SubmittedBy != null);
        submitted.Should().OnlyContain(r => !string.IsNullOrEmpty(r.SubmissionReference));
        // RA-739: each item has its own reference, numbered within its request.
        ItemsOf(submitted).Should().OnlyContain(i => !string.IsNullOrEmpty(i.CaseManagementReference));
        ItemsOf(submitted).Select(i => i.CaseManagementReference).Should().OnlyHaveUniqueItems();
        submitted.Select(r => r.SubmissionReference).Should().OnlyHaveUniqueItems();
        ItemsOf(submitted).Should().NotContain(i => i.Status == ChangeItemStatus.InProgress);
    }

    // RA-739: a submitted add-ORS request holds what a real submit stores: each site's own charge (the per-site
    // in-year ORS fee) and the total. The draft has neither.
    [Fact]
    public async Task SeedAsync_SubmittedAddOrsRequestsCarryEachSitesChargeAndTheTotal()
    {
        var requests = await SeedAndList(ExporterApp);

        var addOrs = requests
            .Where(r => r.ChangeType == AccreditationChangeRequest.AddOverseasSiteChangeType)
            .ToList();
        var submitted = addOrs.Where(r => r.Status == ChangeRequestStatus.Submitted).ToList();
        submitted.Should().NotBeEmpty();
        // Each seeded add-ORS request adds one site.
        submitted.Should().OnlyContain(r => r.Sites.Count == 1 && r.ChargePence == 32800);
        submitted.SelectMany(r => r.Sites).Should().OnlyContain(s => s.ChargePence == 32800);
        var draft = addOrs.Single(r => r.Status == ChangeRequestStatus.InProgress);
        draft.ChargePence.Should().BeNull();
        draft.Sites.Should().OnlyContain(s => s.ChargePence == null);
    }

    // Queried, Submitted, Updated and Duly made: one each to the accredited ORSs' updates and the tonnage item.
    [Fact]
    public async Task SeedAsync_EachOrsHasAtMostOneActiveItem_AndTheOtherActiveStatusesOneEach()
    {
        var requests = await SeedAndList(ExporterApp);
        // Only one change per ORS can be active, whichever kind of request holds it.
        var registrationOnly = await _requests.ListForRegistrationAsync(
            Org,
            DevExporterOrg.Manage.RegistrationId.ToString(),
            MaterialType.Plastic,
            Year,
            Ct
        );

        var activeSites = requests
            .Concat(registrationOnly)
            .SelectMany(r => r.Sites)
            .Where(s => s.Status.IsActive())
            .ToList();
        activeSites.Select(s => s.OrsId).Should().OnlyHaveUniqueItems();
        activeSites.Select(s => s.OrsId).Should().BeEquivalentTo("001", "002", "003", "004");
        var activeItems = ItemsOf(requests).Where(i => i.Status.IsActive()).ToList();
        activeItems.Select(i => i.Status)
            .Should()
            .BeEquivalentTo(
                [
                    ChangeItemStatus.InProgress,
                    ChangeItemStatus.Queried,
                    ChangeItemStatus.Submitted,
                    ChangeItemStatus.Updated,
                    ChangeItemStatus.DulyMade,
                ]
            );
        activeItems.OfType<TonnageBandUpgradeItem>().Should().ContainSingle();
    }

    // New ORS accreditation only for the registered-only ORS, ORS update only for accredited ones, and
    // nothing for a never-registered site.
    [Fact]
    public async Task SeedAsync_ItemTypesMatchTheirSitesAccreditationStatus()
    {
        var requests = await SeedAndList(ExporterApp);

        foreach (var item in requests.SelectMany(r => r.Sites))
        {
            var site = ExporterSites.Single(s => s.SiteId == item.SiteId);
            site.IsNewSite.Should().BeFalse();
            (item.OrsId, item.SiteName).Should().Be((site.OrsId, site.SiteName));
            item.ChangeType.Should()
                .Be(
                    site.Selected
                        ? AccreditationChangeRequest.UpdateOverseasSiteChangeType
                        : AccreditationChangeRequest.AddOverseasSiteChangeType
                );
        }
        requests.Should()
            .OnlyContain(r =>
                r.Sites.Count == 0
                    ? r.TonnageBandUpgrade != null && r.ChangeType == AccreditationChangeRequest.UpgradeTonnageBandChangeType
                    : r.TonnageBandUpgrade == null && r.Sites.All(s => s.ChangeType == r.ChangeType)
            );
    }

    [Fact]
    public async Task SeedAsync_GrantedAndWithdrawnItemsHaveTheirValidityDates()
    {
        var items = ItemsOf(await SeedAndList(ExporterApp));

        items.Where(i => i.Status == ChangeItemStatus.Approved)
            .Should()
            .OnlyContain(i => i.ValidFrom != null && i.ValidFrom.Value.Year == Year);
        items.Where(i => i.Status == ChangeItemStatus.Withdrawn)
            .Should()
            .OnlyContain(i => i.WithdrawnAt != null && i.WithdrawnAt.Value.Year == Year)
            .And.OnlyContain(i => i.ValidFrom == null || i.WithdrawnAt > i.ValidFrom);
        items.Where(i => i.Status != ChangeItemStatus.Withdrawn).Should().OnlyContain(i => i.WithdrawnAt == null);
        items.Where(i => i.Status is not (ChangeItemStatus.Approved or ChangeItemStatus.Withdrawn))
            .Should()
            .OnlyContain(i => i.ValidFrom == null);
    }

    // RA-697: Dragon's site is registered only, so its withdrawn New ORS accreditation was never granted.
    [Fact]
    public async Task SeedAsync_DragonsWithdrawnNewOrsAccreditationHasNoValidFrom()
    {
        var items = ItemsOf(await SeedAndList(ExporterApp));

        items.OfType<ChangeRequestSite>()
            .Where(i => i.OrsId == "004" && i.Status == ChangeItemStatus.Withdrawn)
            .Should()
            .ContainSingle()
            .Which.ValidFrom.Should()
            .BeNull();
    }

    [Fact]
    public async Task SeedAsync_GivesTheReprocessorTonnageBandUpgradesOnly()
    {
        var requests = await SeedAndList(ReprocessorApp);

        requests.Should().NotBeEmpty();
        requests.Should().OnlyContain(r => r.Sites.Count == 0 && r.TonnageBandUpgrade != null);
        requests.Should().OnlyContain(r => r.ChangeType == AccreditationChangeRequest.UpgradeTonnageBandChangeType);
        requests.Should().OnlyContain(r => r.Status == ChangeRequestStatus.Submitted && r.SubmissionReference != null);
        requests.Should().OnlyContain(r => r.TonnageBandUpgrade!.CaseManagementReference == r.SubmissionReference + "-1");
        requests.Should().OnlyContain(r => r.MaterialType == MaterialType.Glass && r.RegistrationId == "bbb000000000000000050018");
        ItemsOf(requests).Where(i => i.Status.IsActive()).Should().ContainSingle();
    }

    // RA-492's org and the empty org keep no change requests, so their specs and empty states hold.
    [Theory]
    [InlineData(50019)]
    [InlineData(50020)]
    public async Task SeedAsync_SeedsNothingForTheOtherOrgs(int orgId)
    {
        var org = DevExporterOrg.Find(orgId)!;

        await CreateSut().SeedAsync(Ct);

        (await _requests.ListAsync(
            org.OrganisationId,
            DevExporterAccreditationSeedService.ApplicationIdFor(org, Year).ToString(),
            Ct
        ))
            .Should()
            .BeEmpty();
    }

    private Task<List<AccreditationChangeRequest>> RegistrationOnlyRequests(int year = Year) =>
        _requests.ListForRegistrationAsync(
            DevExporterOrg.RegistrationOnly.OrganisationId,
            DevExporterOrg.RegistrationOnly.RegistrationId.ToString(),
            MaterialType.Plastic,
            year,
            Ct
        );

    // RA-697 task 011: org 50021's registration-only ORS updates, one item each, across the statuses the list and
    // its filters show, and none In progress, so RA-492's draft links never appear for it.
    [Fact]
    public async Task SeedAsync_GivesTheRegistrationOnlyExporterFiveRegistrationOnlyOrsUpdates()
    {
        await CreateSut().SeedAsync(Ct);

        var requests = await RegistrationOnlyRequests();

        requests.Should().HaveCount(5);
        requests.Should().OnlyContain(r => r.ApplicationId == null && r.AccreditationReference == null);
        requests.Should().OnlyContain(r => r.ChangeType == AccreditationChangeRequest.UpdateOverseasSiteChangeType);
        requests.Should().OnlyContain(r => r.Sites.Count == 1 && r.TonnageBandUpgrade == null);
        requests.Should().OnlyContain(r => r.Status == ChangeRequestStatus.Submitted && r.SubmittedBy != null);
        requests.Select(r => r.SubmissionReference).Should().OnlyHaveUniqueItems().And.NotContainNulls();
        requests.Should().OnlyContain(r => r.Sites.Single().CaseManagementReference == r.SubmissionReference + "-1");
        requests.Should().OnlyContain(r => r.SubmissionReference!.StartsWith("CR26050021A"));
        var items = ItemsOf(requests).OfType<ChangeRequestSite>().ToList();
        items.Select(i => i.Status)
            .Should()
            .BeEquivalentTo(
                [
                    ChangeItemStatus.Submitted,
                    ChangeItemStatus.Queried,
                    ChangeItemStatus.DulyMade,
                    ChangeItemStatus.Approved,
                    ChangeItemStatus.Rejected,
                ]
            );
        items.Should().OnlyContain(i => i.ChangeType == AccreditationChangeRequest.UpdateOverseasSiteChangeType);
        items.Should().OnlyContain(i => (i.ValidFrom != null) == (i.Status == ChangeItemStatus.Approved));
        items.Where(i => i.Status.IsActive()).Select(i => i.OrsId).Should().OnlyHaveUniqueItems();
        requests.Select(r => r.RequestId).Should().Contain("00002026-0000-0000-a000-050021000001");
    }

    // The items name the seeded ORS records, keyed by the ORS id's integer value as the registration view keys them.
    [Fact]
    public async Task SeedAsync_RegistrationOnlyItemsNameTheSeededOrsRecords()
    {
        await CreateSut().SeedAsync(Ct);
        var sites = RegisteredSiteMapper.ToSites(
            DevExporterAccreditationSeedService.RegisteredOverseasSites(
                DevExporterOrg.RegistrationOnly,
                MongoDB.Bson.ObjectId.Empty
            )
        );

        var items = ItemsOf(await RegistrationOnlyRequests()).OfType<ChangeRequestSite>().ToList();

        items.Should()
            .OnlyContain(i => sites.Any(s => s.SiteId == i.SiteId && s.OrsId == i.OrsId && s.SiteName == i.SiteName));
        items.Select(i => i.OrsId).Distinct().Should().HaveCount(4);
    }

    // RA-697 task 014: 50018's exporter also has one registration-only ORS update (no accreditation) on Dragon -
    // registered only, and in the accreditation's site list. Keyed by the ORS id's integer value, as every
    // registration-only item is, and Refused, so Dragon's draft stays its one active item.
    [Fact]
    public async Task SeedAsync_GivesTheAccreditedExporterOneRegistrationOnlyOrsUpdateOnDragon()
    {
        await CreateSut().SeedAsync(Ct);

        var requests = await _requests.ListForRegistrationAsync(
            Org,
            DevExporterOrg.Manage.RegistrationId.ToString(),
            MaterialType.Plastic,
            Year,
            Ct
        );

        var request = requests.Should().ContainSingle().Subject;
        request.RequestId.Should().Be("00002026-0000-0000-a000-050018000012");
        (request.ApplicationId, request.AccreditationReference).Should().Be((null, null));
        request.ChangeType.Should().Be(AccreditationChangeRequest.UpdateOverseasSiteChangeType);
        request.Status.Should().Be(ChangeRequestStatus.Submitted);
        request.SubmissionReference.Should().Be("CR26050018A012");
        var item = request.Sites.Should().ContainSingle().Subject;
        (item.SiteId, item.OrsId, item.SiteName, item.Status)
            .Should()
            .Be((4, "004", "Dragon Recyclers", ChangeItemStatus.Rejected));
        item.CaseManagementReference.Should().Be("CR26050018A012-1");
        (await _requests.ListAsync(Org, ExporterApp, Ct)).Should().NotContain(r => r.RequestId == request.RequestId);
    }

    // Keyed to that year's accreditation: after New Year a running stack keeps last year's requests on
    // last year's accreditation and seeds the new year's afresh.
    [Fact]
    public async Task SeedAsync_InTheNextYear_SeedsThatYearsAccreditationAndKeepsLastYears()
    {
        await CreateSut().SeedAsync(Ct);
        var lastYears = await _requests.ListAsync(Org, ExporterApp, Ct);

        await CreateSut(clock: new TestClock(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc))).SeedAsync(Ct);

        (await _requests.ListAsync(Org, ExporterApp, Ct)).Should().HaveCount(lastYears.Count);
        var nextYears = await _requests.ListAsync(
            Org,
            DevExporterAccreditationSeedService.ApplicationIdFor(DevExporterOrg.Manage, 2027).ToString(),
            Ct
        );
        nextYears.Should().HaveCount(lastYears.Count);
        nextYears.Should().OnlyContain(r => r.Year == 2027 && r.AccreditationReference == "A27EX0500180001PL");
        nextYears.Select(r => r.RequestId).Should().NotIntersectWith(lastYears.Select(r => r.RequestId));
        ItemsOf(nextYears).Should().OnlyContain(i => i.ValidFrom == null || i.ValidFrom.Value.Year == 2027);
    }

    [Fact]
    public async Task SeedAsync_RunTwice_SeedsEachRequestOnce()
    {
        await CreateSut().SeedAsync(Ct);
        var first = await _requests.ListAsync(Org, ExporterApp, Ct);

        await CreateSut().SeedAsync(Ct);

        var second = await _requests.ListAsync(Org, ExporterApp, Ct);
        second.Select(r => r.RequestId).Should().BeEquivalentTo(first.Select(r => r.RequestId));
        ItemsOf(second).Select(i => i.ItemId).Should().BeEquivalentTo(ItemsOf(first).Select(i => i.ItemId));
        (await _requests.ListAsync(Org, ReprocessorApp, Ct)).Should().HaveCount(3);
    }

    // A tester who submits the seeded draft, then starts another, keeps both on a rerun.
    [Fact]
    public async Task SeedAsync_RequestsChangedByATester_AreLeftAlone()
    {
        await CreateSut().SeedAsync(Ct);
        var draft = (await _requests.GetDraftAsync(Org, ExporterApp, Ct))!;
        draft.Status = ChangeRequestStatus.Submitted;
        draft.SetItemStatuses(ChangeItemStatus.Submitted);
        (await _requests.UpdateAsync(draft, Ct)).Should().BeTrue();
        var testersDraft = new AccreditationChangeRequest
        {
            OrganisationId = Org,
            ApplicationId = ExporterApp,
            MaterialType = MaterialType.Plastic,
            Year = Year,
        };
        (await _requests.InsertAsync(testersDraft, Ct)).Should().BeTrue();
        var count = (await _requests.ListAsync(Org, ExporterApp, Ct)).Count;

        await CreateSut().SeedAsync(Ct);

        var requests = await _requests.ListAsync(Org, ExporterApp, Ct);
        requests.Should().HaveCount(count);
        requests.Single(r => r.RequestId == draft.RequestId).Status.Should().Be(ChangeRequestStatus.Submitted);
        (await _requests.GetDraftAsync(Org, ExporterApp, Ct))!.RequestId.Should().Be(testersDraft.RequestId);
    }

    [Fact]
    public async Task SeedAsync_StoreThrows_LogsAndDoesNotThrow()
    {
        var requests = Substitute.For<IChangeRequestPersistence>();
        requests
            .InsertAsync(Arg.Any<AccreditationChangeRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        var act = () => CreateSut(requests).SeedAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // Through RA-492's GET, with RA-425's accreditation unchanged: the accredited and registered tabs
    // keep their counts, Dragon shows as in progress, and the other ORSs are pending.
    [Fact]
    public async Task Get_ShowsTheSeededRequestsAgainstTheUnchangedAccreditation()
    {
        using var factory = new AccreditationChangeTestFactory();
        var accreditation = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.Manage, Year);
        factory.FakePersistence.Seed(accreditation);
        factory.FakePersistence.Seed(
            DevExporterAccreditationSeedService.ReprocessorAccreditation(DevExporterOrg.Manage, Year)
        );
        await CreateSut(factory.FakeChangeRequests).SeedAsync(Ct);
        using var client = factory.CreateClient();

        var view = await GetView(client, ExporterApp);

        var sites = view.GetProperty("sites").EnumerateArray().ToList();
        sites.Select(s =>
                (
                    s.GetProperty("orsId").GetString(),
                    s.GetProperty("accreditationStatus").GetString(),
                    s.GetProperty("pendingRequestStatus").GetString()
                )
            )
            .Should()
            .Equal(
                ("001", "Accredited", "Submitted"),
                ("002", "Accredited", "Submitted"),
                ("003", "Accredited", "Submitted"),
                ("004", "Registered", "InProgress")
            );
        view.GetProperty("pendingOrsIds").EnumerateArray().Select(e => e.GetString())
            .Should()
            .BeEquivalentTo("001", "002", "003", "004");
        view.GetProperty("activeRequest").GetProperty("sites")[0].GetProperty("orsId").GetString().Should().Be("004");
        // RA-697 task 014: the registration-only ORS update is merged in: 14 items.
        var requests = view.GetProperty("requests").EnumerateArray().ToList();
        requests
            .Sum(r =>
                r.GetProperty("sites").GetArrayLength()
                + (r.GetProperty("tonnageBandUpgrade").ValueKind == JsonValueKind.Null ? 0 : 1)
            )
            .Should()
            .Be(14);
        requests.Select(r => r.GetProperty("requestId").GetString())
            .Should()
            .Contain("00002026-0000-0000-a000-050018000012");
        (await factory.FakePersistence.GetByIdAsync(Org, ExporterApp))!.OverseasSites!.Sites
            .Select(s => (s.SiteId, s.Selected, s.ValidFrom))
            .Should()
            .Equal(accreditation.OverseasSites!.Sites.Select(s => (s.SiteId, s.Selected, s.ValidFrom)));

        var reprocessor = await GetView(client, ReprocessorApp);
        reprocessor.GetProperty("sites").GetArrayLength().Should().Be(0);
        reprocessor.GetProperty("requests")
            .EnumerateArray()
            .Should()
            .OnlyContain(r => r.GetProperty("tonnageBandUpgrade").GetProperty("requestedBand").GetString() != null);
    }

    private static async Task<JsonElement> GetView(HttpClient client, string applicationId)
    {
        var response = await client.GetAsync($"/api/v1/accreditation-changes/{Org}/{applicationId}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
