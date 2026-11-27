using System.Net;
using System.Net.Http.Json;
using System.Text;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-495: Case Management pushes each add-ORS work item's status changes to the change request item it was raised
// for, as it does an accreditation application's (AccreditationApplicationEndpoints' case-management status route).
// The item is org 50019's request for 901910 (DevExporterAccreditationSeedService), submitted and given a work item.
public sealed class ChangeRequestStatusEndpointTests : IDisposable
{
    private const int Year = 2026;
    private const int SiteId = 901910;
    private static readonly string Org = DevExporterOrg.InYearChange.OrganisationId;
    private static readonly string AppId = DevExporterAccreditationSeedService
        .ApplicationIdFor(DevExporterOrg.InYearChange, Year)
        .ToString();
    private static readonly string Sites = $"/api/v1/accreditation-changes/{Org}/{AppId}/requests/current/sites";
    private static readonly DateTime Watermark = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SubmittedAt = Watermark.AddDays(-1);

    // AC6: the date the caseworker entered, deliberately not the submission date.
    private static readonly DateOnly EffectiveDate = new(2026, 5, 20);
    private static readonly DateTime AccreditedFrom = new(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AccreditationChangeTestFactory _factory;
    private readonly HttpClient _client;
    private readonly Guid _workItemId = Guid.NewGuid();
    private readonly Guid _siblingWorkItemId = Guid.NewGuid();

    public ChangeRequestStatusEndpointTests()
    {
        _factory = new AccreditationChangeTestFactory();
        _factory.FakePersistence.Seed(
            DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year)
        );
        _factory.FakePersistence.Seed(_updateAccreditation);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static string StatusUrl(Guid workItemId) =>
        "/" + ChangeRequestStatusEndpoints.Route.Replace("{workItemId}", workItemId.ToString());

    // A submitted request for 901910 and 901914, each with its own work item; 901910's item is the one pushed to.
    private AccreditationChangeRequest SeedSubmittedRequest(
        ChangeItemStatus status = ChangeItemStatus.Submitted,
        DateTime? watermark = null,
        string changeType = AccreditationChangeRequest.AddOverseasSiteChangeType
    )
    {
        var request = new AccreditationChangeRequest
        {
            OrganisationId = Org,
            ApplicationId = AppId,
            MaterialType = MaterialType.Plastic,
            Year = Year,
            Status = ChangeRequestStatus.Submitted,
            SubmittedAt = SubmittedAt,
            Sites =
            [
                new ChangeRequestSite
                {
                    ChangeType = changeType,
                    SiteId = SiteId,
                    OrsId = "005",
                    SiteName = "Lyon",
                    Status = status,
                    CaseManagementWorkItemId = _workItemId,
                    CaseManagementStatusUpdatedAt = watermark,
                },
                new ChangeRequestSite
                {
                    SiteId = 901914,
                    OrsId = "009",
                    SiteName = "Rotterdam",
                    Status = ChangeItemStatus.Submitted,
                    CaseManagementWorkItemId = _siblingWorkItemId,
                },
            ],
        };
        _factory.FakeChangeRequests.Seed(request);
        return request;
    }

    // Case Management sends the effective date on an approval only.
    private Task<HttpResponseMessage> Push(string toStateId, DateTime occurredAt, Guid? workItemId = null) =>
        Push(toStateId, occurredAt, toStateId == "approved" ? EffectiveDate : null, workItemId);

    private Task<HttpResponseMessage> Push(
        string toStateId,
        DateTime occurredAt,
        DateOnly? effectiveDate,
        Guid? workItemId = null
    ) =>
        _client.PostAsJsonAsync(
            StatusUrl(workItemId ?? _workItemId),
            new
            {
                toStateId,
                toStateDisplayName = toStateId,
                actionId = "some-action",
                actionDisplayName = "Some action",
                occurredAt,
                effectiveDate,
            },
            Ct
        );

    private ChangeRequestSite Item(Guid? workItemId = null) =>
        _factory
            .FakeChangeRequests.All.SelectMany(r => r.Sites)
            .Single(s => s.CaseManagementWorkItemId == (workItemId ?? _workItemId));

    // ---------------------------------------------------------------- mapping

    [Theory]
    [InlineData("submitted", ChangeItemStatus.Submitted)]
    [InlineData("duly-made", ChangeItemStatus.DulyMade)]
    [InlineData("assessment-started", ChangeItemStatus.DulyMade)]
    [InlineData("updated", ChangeItemStatus.Updated)]
    [InlineData("assessment-in-progress", ChangeItemStatus.Updated)]
    [InlineData("rejected", ChangeItemStatus.Rejected)]
    public async Task MappedState_SetsTheItemsStatusAndAdvancesItsWatermark(
        string toStateId,
        ChangeItemStatus expected
    )
    {
        SeedSubmittedRequest(watermark: Watermark);
        var occurredAt = Watermark.AddMinutes(5);

        var response = await Push(toStateId, occurredAt);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(expected);
        Item().CaseManagementStatusUpdatedAt.Should().Be(occurredAt);
        // Only that item: its sibling in the same request is its own work item.
        Item(_siblingWorkItemId).Status.Should().Be(ChangeItemStatus.Submitted);
        Item(_siblingWorkItemId).CaseManagementStatusUpdatedAt.Should().BeNull();
        _factory.FakeChangeRequests.All.Single().Status.Should().Be(ChangeRequestStatus.Submitted);
    }

    [Theory]
    [InlineData("awaiting-decision")]
    [InlineData("approved-pending-contact")]
    [InlineData("something-new")]
    public async Task UnmappedState_LeavesTheStatusButAdvancesTheWatermark(string toStateId)
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var occurredAt = Watermark.AddMinutes(5);

        var response = await Push(toStateId, occurredAt);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        Item().CaseManagementStatusUpdatedAt.Should().Be(occurredAt);
    }

    [Fact]
    public async Task FirstPush_WithNoWatermark_IsApplied()
    {
        SeedSubmittedRequest();

        (await Push("duly-made", Watermark)).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        Item().CaseManagementStatusUpdatedAt.Should().Be(Watermark);
    }

    [Fact]
    public async Task Response_IsTheUpdatedItem()
    {
        SeedSubmittedRequest();

        var response = await Push("duly-made", Watermark);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        body.GetProperty("itemId").GetGuid().Should().Be(Item().ItemId);
        body.GetProperty("status").GetString().Should().Be("DulyMade");
    }

    // ---------------------------------------------------------------- guards

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PushAtOrBeforeTheWatermark_Returns200AndWritesNothing(int secondsAfter)
    {
        SeedSubmittedRequest(ChangeItemStatus.Updated, Watermark);

        var response = await Push("duly-made", Watermark.AddSeconds(secondsAfter));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(ChangeItemStatus.Updated);
        Item().CaseManagementStatusUpdatedAt.Should().Be(Watermark);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task UnknownWorkItem_Returns404()
    {
        SeedSubmittedRequest();

        var response = await Push("duly-made", Watermark, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    [Theory]
    [InlineData(ChangeItemStatus.Approved, "rejected")]
    [InlineData(ChangeItemStatus.Approved, "duly-made")]
    [InlineData(ChangeItemStatus.Rejected, "approved")]
    [InlineData(ChangeItemStatus.Rejected, "updated")]
    [InlineData(ChangeItemStatus.Withdrawn, "approved")]
    [InlineData(ChangeItemStatus.Withdrawn, "duly-made")]
    public async Task TerminalItem_MappedPushToAnotherStatus_Returns409(ChangeItemStatus status, string toStateId)
    {
        SeedSubmittedRequest(status, Watermark);

        var response = await Push(toStateId, Watermark.AddMinutes(5));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Item().Status.Should().Be(status);
        Item().CaseManagementStatusUpdatedAt.Should().Be(Watermark);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // Case Management's confirm-contact step retries with a newer occurredAt when its own write fails after the
    // push landed, so a repeat of the final status the item already has is accepted, without a write.
    [Theory]
    [InlineData(ChangeItemStatus.Approved, "approved")]
    [InlineData(ChangeItemStatus.Rejected, "rejected")]
    public async Task TerminalItem_RepeatOfItsFinalStatus_Returns200AndWritesNothing(
        ChangeItemStatus status,
        string toStateId
    )
    {
        SeedSubmittedRequest(status, Watermark);

        var response = await Push(toStateId, Watermark.AddMinutes(5));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(status);
        Item().CaseManagementStatusUpdatedAt.Should().Be(Watermark);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // RA-800: Case Management can correct the valid-from date until its confirm contact commits, which may be after
    // the grant landed here. Accepting the repeat would leave it showing one date and the site accredited from
    // another, so a repeat naming a different date is refused and its confirmation does not complete.
    [Fact]
    public async Task ApprovedAgain_WithADifferentEffectiveDate_Returns409AndWritesNothing()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = Item();
        var updates = _factory.FakeChangeRequests.UpdateCount;
        var legacyVersion = Legacy().Version;

        var response = await Push("approved", Watermark.AddMinutes(10), EffectiveDate.AddDays(1));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("2026-05-20");
        Item().Should().BeEquivalentTo(granted);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(updates);
        Legacy().Version.Should().Be(legacyVersion);
        LegacySite(SiteId).ValidFrom.Should().Be(SiteApprovalDate.Format(AccreditedFrom));
    }

    // An unmapped push changes no status, so a terminal item takes it: only its watermark moves.
    [Fact]
    public async Task TerminalItem_UnmappedPush_AdvancesOnlyTheWatermark()
    {
        SeedSubmittedRequest(ChangeItemStatus.Rejected, Watermark);
        var occurredAt = Watermark.AddMinutes(5);

        (await Push("something-new", occurredAt)).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.Rejected);
        Item().CaseManagementStatusUpdatedAt.Should().Be(occurredAt);
    }

    [Theory]
    [InlineData(ChangeItemStatus.InProgress, "approved")]
    [InlineData(ChangeItemStatus.Queried, "approved")]
    [InlineData(ChangeItemStatus.InProgress, "rejected")]
    [InlineData(ChangeItemStatus.Queried, "rejected")]
    public async Task ApproveOrReject_FromOutsideAnActiveAssessmentStatus_Returns409(
        ChangeItemStatus status,
        string toStateId
    )
    {
        SeedSubmittedRequest(status, Watermark);

        var response = await Push(toStateId, Watermark.AddMinutes(5));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Item().Status.Should().Be(status);
    }

    [Theory]
    [InlineData(ChangeItemStatus.Submitted)]
    [InlineData(ChangeItemStatus.DulyMade)]
    [InlineData(ChangeItemStatus.Updated)]
    public async Task Reject_FromAnActiveAssessmentStatus_Succeeds(ChangeItemStatus status)
    {
        SeedSubmittedRequest(status, Watermark);

        (await Push("rejected", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.Rejected);
    }

    // ---------------------------------------------------------------- grant (RA-495 task 007)

    private AccreditationApplicationModel Legacy() => _factory.FakePersistence.GetByIdAsync(Org, AppId).Result!;

    private OverseasSiteModel LegacySite(int siteId) =>
        Legacy().OverseasSites!.Sites.Single(s => s.SiteId == siteId);

    [Theory]
    [InlineData(ChangeItemStatus.Submitted)]
    [InlineData(ChangeItemStatus.DulyMade)]
    [InlineData(ChangeItemStatus.Updated)]
    public async Task Approved_GrantsTheItemFromTheEffectiveDate(ChangeItemStatus status)
    {
        SeedSubmittedRequest(status, Watermark);
        var occurredAt = Watermark.AddMinutes(5);

        var response = await Push("approved", occurredAt);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(ChangeItemStatus.Approved);
        Item().ValidFrom.Should().Be(AccreditedFrom);
        Item().CaseManagementStatusUpdatedAt.Should().Be(occurredAt);
        Item(_siblingWorkItemId).Status.Should().Be(ChangeItemStatus.Submitted);
        Item(_siblingWorkItemId).ValidFrom.Should().BeNull();
    }

    // PR #297 review: toStateId is a required string, but an explicit null or a blank still binds. The item route
    // used to map it to "no status" and advance the watermark; now it is a 400 ProblemDetails, logged at Error with
    // the ids, before anything is read or written.
    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public async Task NoToStateId_Returns400AndLogsErrorAndWritesNothing(string toStateIdJson)
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var logs = new CapturingLoggerFactory();
        using var client = _factory
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(logs))
            )
            .CreateClient();
        using var body = new StringContent(
            $$"""{"toStateId":{{toStateIdJson}},"actionId":"some-action","occurredAt":"{{Watermark.AddMinutes(5):O}}"}""",
            Encoding.UTF8,
            "application/json"
        );

        var response = await client.PostAsync(StatusUrl(_workItemId), body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(CaseManagementStatusMapping.MissingToStateIdDetail);
        var entry = logs.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error).Subject;
        entry.Message.Should().Contain("ChangeRequestStatusChangedFromCaseManagement: status push has no toStateId");
        entry.Message.Should().Contain($"workItemId={_workItemId}");
        entry.Message.Should().Contain("actionId=some-action");
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        Item().CaseManagementStatusUpdatedAt.Should().Be(Watermark);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // An unknown toStateId still changes no item status and only advances the watermark, but is now logged at Error
    // with the ids. awaiting-decision (in the shared table, no item equivalent) and queried (a deadline change on a
    // queried item, CaseManagementStatusMapping.KnownIgnoredStateIds) are known and not logged.
    [Theory]
    [InlineData("unmapped-future-state", true)]
    [InlineData("awaiting-decision", false)]
    [InlineData("queried", false)]
    public async Task StateWithNoItemStatus_ChangesNoStatusAndLogsErrorOnlyWhenUnknown(string toStateId, bool loggedAsUnknown)
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var logs = new CapturingLoggerFactory();
        using var client = _factory
            .WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(logs))
            )
            .CreateClient();
        var occurredAt = Watermark.AddMinutes(5);

        var response = await client.PostAsJsonAsync(
            StatusUrl(_workItemId),
            new { toStateId, actionId = "some-action", occurredAt },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        Item().CaseManagementStatusUpdatedAt.Should().Be(occurredAt);
        var errors = logs.Entries.Where(e => e.LogLevel == LogLevel.Error).ToList();
        if (!loggedAsUnknown)
        {
            errors.Should().BeEmpty();
            return;
        }
        var entry = errors.Should().ContainSingle().Subject;
        entry.Message.Should().Contain($"ChangeRequestStatusChangedFromCaseManagement: status push to unknown toStateId={toStateId}");
        entry.Message.Should().Contain($"workItemId={_workItemId}");
        entry.Message.Should().Contain("actionId=some-action");
    }

    // PR #297 review: the item route projects the application's table (CaseManagementStatusMapping.StatusPushMap)
    // rather than keeping its own copy of the state ids. Every entry projects, so a state id added to the table can't
    // reach an item push and throw; awaiting-decision has no item equivalent and leaves the status alone.
    [Fact]
    public void MapState_ProjectsEveryEntryOfTheSharedTable()
    {
        var projected = CaseManagementStatusMapping.StatusPushMap.ToDictionary(
            entry => entry.Key,
            entry => ChangeRequestStatusEndpoints.MapState(entry.Key)
        );

        projected
            .Should()
            .Equal(
                new Dictionary<string, ChangeItemStatus?>
                {
                    ["submitted"] = ChangeItemStatus.Submitted,
                    ["duly-made"] = ChangeItemStatus.DulyMade,
                    ["assessment-started"] = ChangeItemStatus.DulyMade,
                    ["assessment-in-progress"] = ChangeItemStatus.Updated,
                    ["updated"] = ChangeItemStatus.Updated,
                    ["awaiting-decision"] = null,
                    ["approved"] = ChangeItemStatus.Approved,
                    ["rejected"] = ChangeItemStatus.Rejected,
                }
            );
        ChangeRequestStatusEndpoints.MapState("unmapped-future-state").Should().BeNull();
    }

    // AC6: only Case Management knows the date the caseworker entered, so an approval without it grants nothing.
    [Fact]
    public async Task Approved_WithoutAnEffectiveDate_Returns400AndWritesNothing()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);

        var response = await Push("approved", Watermark.AddMinutes(5), effectiveDate: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("effectiveDate");
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        Item().ValidFrom.Should().BeNull();
        Item().CaseManagementStatusUpdatedAt.Should().Be(Watermark);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
        LegacySite(SiteId).Selected.Should().BeFalse();
    }

    // The registered site is already on the legacy accreditation application, unselected: the grant selects it,
    // accredited from the effective date Case Management sent, and adds no site.
    [Fact]
    public async Task Approved_SelectsTheSiteOnTheAccreditationFromTheEffectiveDate()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var sitesBefore = Legacy().OverseasSites!.Sites.Count;
        LegacySite(SiteId).Selected.Should().BeFalse();

        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        LegacySite(SiteId).Selected.Should().BeTrue();
        LegacySite(SiteId).ValidFrom.Should().Be(SiteApprovalDate.Format(AccreditedFrom));
        Legacy().OverseasSites!.Sites.Should().HaveCount(sitesBefore);
        // Only the granted site: its sibling item is still being assessed.
        LegacySite(901914).Selected.Should().BeFalse();
        LegacySite(901914).ValidFrom.Should().BeNull();
    }

    // AC: the site is on the Accredited tab, the request shows the item granted from its effective date, and the
    // site is no longer held by the request (the add-site check now refuses it as accredited, not as pending).
    [Fact]
    public async Task Approved_SiteIsAccreditedAndNoLongerPending()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);

        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        var view = await _client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/v1/accreditation-changes/{Org}/{AppId}",
            Ct
        );
        var site = view
            .GetProperty("sites")
            .EnumerateArray()
            .Single(s => s.GetProperty("siteId").GetInt32() == SiteId);
        site.GetProperty("accreditationStatus").GetString().Should().Be("Accredited");
        site.GetProperty("pendingRequestId").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        view.GetProperty("pendingOrsIds").EnumerateArray().Select(e => e.GetString()).Should().NotContain("005");
        var item = view
            .GetProperty("requests")
            .EnumerateArray()
            .SelectMany(r => r.GetProperty("sites").EnumerateArray())
            .Single(s => s.GetProperty("siteId").GetInt32() == SiteId);
        item.GetProperty("status").GetString().Should().Be("Approved");
        item.GetProperty("validFrom").GetDateTime().ToUniversalTime().Should().Be(AccreditedFrom);

        var again = await _client.PostAsJsonAsync(Sites, new { siteId = SiteId }, Ct);
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync(Ct)).Should().Contain("already approved");
    }

    [Fact]
    public async Task ApprovedAgain_ChangesNothing()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = Item();
        var updates = _factory.FakeChangeRequests.UpdateCount;
        var legacyVersion = Legacy().Version;

        (await Push("approved", Watermark.AddMinutes(10))).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Should().BeEquivalentTo(granted);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(updates);
        Legacy().Version.Should().Be(legacyVersion);
    }

    // The site the item was raised for has gone from the accreditation application: nothing to grant, and a
    // retry would fail the same way, so a 409 with nothing written.
    [Fact]
    public async Task Approved_SiteNotOnTheAccreditation_Returns409AndWritesNothing()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var application = Legacy();
        application.OverseasSites!.Sites.RemoveAll(s => s.SiteId == SiteId);
        await _factory.FakePersistence.UpdateAsync(application);

        var response = await Push("approved", Watermark.AddMinutes(5));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        Item().CaseManagementStatusUpdatedAt.Should().Be(Watermark);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // A site already accredited some other way is not this grant's to make: granting it would change nothing on the
    // site yet approve the item, so a 409 with nothing written.
    [Fact]
    public async Task Approved_SiteAlreadyAccreditedAnotherWay_Returns409AndWritesNothing()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var application = Legacy();
        var site = application.OverseasSites!.Sites.Single(s => s.SiteId == SiteId);
        site.Selected = true;
        site.ValidFrom = "2025-01-01T00:00:00.000Z";
        await _factory.FakePersistence.UpdateAsync(application);
        var versionBefore = Legacy().Version;

        var response = await Push("approved", Watermark.AddMinutes(5));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        Item().ValidFrom.Should().BeNull();
        LegacySite(SiteId).ValidFrom.Should().Be("2025-01-01T00:00:00.000Z");
        Legacy().Version.Should().Be(versionBefore);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // Only an add-ORS or (RA-782) update-ORS item is granted on its site: an approved push for a site item of any
    // other change type is a 409 with nothing written.
    [Fact]
    public async Task Approved_NeitherAnAddNorAnUpdateOrsItem_Returns409AndWritesNothing()
    {
        SeedSubmittedRequest(
            ChangeItemStatus.DulyMade,
            Watermark,
            AccreditationChangeRequest.UpgradeTonnageBandChangeType
        );
        var versionBefore = Legacy().Version;

        var response = await Push("approved", Watermark.AddMinutes(5));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        LegacySite(SiteId).Selected.Should().BeFalse();
        Legacy().Version.Should().Be(versionBefore);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // Without dual-write there is no transaction: a lost race on the item after the site was selected is read
    // again, and the grant, already applied to the site, is not applied twice.
    [Fact]
    public async Task Approved_LostRaceOnTheItem_IsReadAgainAndTheSiteSelectedOnce()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var lost = 0;
        _factory.FakeChangeRequests.LoseUpdateWhen = _ => Interlocked.Increment(ref lost) == 1;
        var versionBefore = Legacy().Version;

        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.Approved);
        LegacySite(SiteId).Selected.Should().BeTrue();
        Legacy().Version.Should().Be(versionBefore + 1);
    }

    // ---------------------------------------------------------------- supplied answers and BES (RA-495 3bb.29)

    private const int DelhiSiteId = 901911;

    private static BesEvidenceFileModel BesFile(string fileId) =>
        new()
        {
            FileId = fileId,
            Filename = $"{fileId}.pdf",
            ContentType = "application/pdf",
            ScanStatus = "Clean",
            BesEvidenceValidFromDate = "2026-01-01",
            BesEvidenceExpiryDate = "2027-01-01",
            S3Key = $"bes/{fileId}",
            S3Bucket = "bes-bucket",
        };

    // Delhi (ORS 006) is registered with no country (cleared here), coordinates, R code or conditions of export
    // answer, outside the EU and OECD: the operator supplied them all on the request, with BES evidence.
    private void SeedSuppliedRequest()
    {
        var application = Legacy();
        application.OverseasSites!.Sites.Single(s => s.SiteId == DelhiSiteId).Country = null;
        _factory.FakePersistence.UpdateAsync(application).Wait(Ct);
        _factory.FakeChangeRequests.Seed(
            new AccreditationChangeRequest
            {
                OrganisationId = Org,
                ApplicationId = AppId,
                MaterialType = MaterialType.Plastic,
                Year = Year,
                Status = ChangeRequestStatus.Submitted,
                SubmittedAt = SubmittedAt,
                Sites =
                [
                    new ChangeRequestSite
                    {
                        SiteId = DelhiSiteId,
                        OrsId = "006",
                        SiteName = "Delhi Polymers Pvt",
                        Status = ChangeItemStatus.DulyMade,
                        CaseManagementWorkItemId = _workItemId,
                        CaseManagementStatusUpdatedAt = Watermark,
                        Answers = new ChangeRequestSiteAnswers
                        {
                            Country = "India",
                            Coordinates = "28.6139, 77.2090",
                            OperationCodes = ["R3"],
                            ConditionsOfExport = false,
                        },
                        BesEvidence = new BesEvidenceModel
                        {
                            BesEvidenceUploads = [BesFile("bes-1"), BesFile("bes-2")],
                            DoYouWantToUploadMoreEvidence = false,
                        },
                    },
                ],
            }
        );
    }

    // As the operator's own add-to-accreditation journey (promote, then upload BES) writes them onto the site.
    [Fact]
    public async Task Approved_WritesTheSuppliedAnswersOntoTheLegacySite()
    {
        SeedSuppliedRequest();

        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        var site = LegacySite(DelhiSiteId);
        site.Selected.Should().BeTrue();
        site.Country.Should().Be("India");
        site.Coordinates.Should().Be("28.6139, 77.2090");
        site.OperationCodes.Should().Equal("R3");
        site.ConditionsOfExport.Should().BeFalse();
        site.IsEu.Should().BeFalse();
        site.IsOecd.Should().BeFalse();
        site.SiteName.Should().Be("Delhi Polymers Pvt");
    }

    [Fact]
    public async Task Approved_AddsTheItemsBesEvidenceToTheLegacySite()
    {
        SeedSuppliedRequest();

        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        var evidence = LegacySite(DelhiSiteId).BesEvidence;
        evidence.Should().NotBeNull();
        evidence!.BesEvidenceUploads.Should().BeEquivalentTo([BesFile("bes-1"), BesFile("bes-2")], o =>
            o.Excluding(f => f.UploadedAt)
        );
    }

    // Only what was supplied: the answers the site already held are left as they are.
    [Fact]
    public async Task Approved_NothingSupplied_LeavesTheSitesOwnAnswers()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        var before = LegacySite(SiteId);

        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        var site = LegacySite(SiteId);
        site.Country.Should().Be(before.Country);
        site.Coordinates.Should().Be(before.Coordinates);
        site.OperationCodes.Should().Equal(before.OperationCodes);
        site.ConditionsOfExport.Should().Be(before.ConditionsOfExport);
        site.IsEu.Should().Be(before.IsEu);
        site.BesEvidence?.BesEvidenceUploads.Should().BeEquivalentTo(before.BesEvidence?.BesEvidenceUploads ?? []);
    }

    // A grant replayed after its item write was lost adds the evidence once.
    [Fact]
    public async Task Approved_LostRaceOnTheItem_AddsTheBesEvidenceOnce()
    {
        SeedSuppliedRequest();
        var lost = 0;
        _factory.FakeChangeRequests.LoseUpdateWhen = _ => Interlocked.Increment(ref lost) == 1;

        (await Push("approved", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Push("approved", Watermark.AddMinutes(10))).StatusCode.Should().Be(HttpStatusCode.OK);

        LegacySite(DelhiSiteId).BesEvidence!.BesEvidenceUploads.Select(f => f.FileId).Should().Equal("bes-1", "bes-2");
    }

    // ---------------------------------------------------------------- concurrency

    // Sibling items share the request document, so their pushes can race: a lost version check is read again.
    [Fact]
    public async Task LostRaceWithAnotherWriter_IsReadAgainAndApplied()
    {
        SeedSubmittedRequest();
        var lost = 0;
        _factory.FakeChangeRequests.LoseUpdateWhen = _ => Interlocked.Increment(ref lost) == 1;

        (await Push("duly-made", Watermark)).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(2);
    }

    // A 5xx, so Case Management's push pipeline retries it.
    [Fact]
    public async Task LostRaceEveryTime_Returns503AndWritesNothing()
    {
        SeedSubmittedRequest();
        _factory.FakeChangeRequests.LoseUpdateWhen = _ => true;

        var response = await Push("duly-made", Watermark);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        Item().Status.Should().Be(ChangeItemStatus.Submitted);
    }

    // ---------------------------------------------------------------- lock

    // A site is held while any item for it is active. Rejected is not, so a refusal frees the site for a new
    // request (the add-site route's own 409 check).
    [Fact]
    public async Task Rejected_ReleasesTheSitesLock()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);
        (await _client.PostAsJsonAsync(Sites, new { siteId = SiteId }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);

        (await Push("rejected", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync(Sites, new { siteId = SiteId }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task UntilTheFinalStatus_TheSiteStaysLocked()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);

        (await Push("awaiting-decision", Watermark.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync(Sites, new { siteId = SiteId }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- ORS update (RA-782)

    // Org 50025: ORS 902501 (001, Gdansk) is accredited with interim sites 902511 (001) and 902512 (002); 902504 is
    // registered only.
    private const int GdanskSiteId = 902501;
    private static readonly string UpdateOrg = DevExporterOrg.InYearUpdate.OrganisationId;
    private static readonly string UpdateAppId = DevExporterAccreditationSeedService
        .ApplicationIdFor(DevExporterOrg.InYearUpdate, Year)
        .ToString();

    // An update has no effective date: it takes effect on the day Case Management decided it.
    private static readonly DateTime DecidedAt = Watermark.AddMinutes(5);
    private static readonly DateTime DecidedOn = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

    // Held by reference by the fake persistence, so a test can change the ORS in place as another write would have.
    private readonly AccreditationApplicationModel _updateAccreditation =
        DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, Year);

    private OverseasSiteModel StoredSite(int siteId = GdanskSiteId) =>
        _updateAccreditation.OverseasSites!.Sites.Single(s => s.SiteId == siteId);

    private AccreditationApplicationModel UpdateLegacy() =>
        _factory.FakePersistence.GetByIdAsync(UpdateOrg, UpdateAppId).Result!;

    private OverseasSiteModel UpdateSite(int siteId = GdanskSiteId) =>
        UpdateLegacy().OverseasSites!.Sites.Single(s => s.SiteId == siteId);

    private static InterimSiteModel InterimOf(OverseasSiteModel site, int interimSiteId) =>
        site.InterimSites.Single(i => i.SiteId == interimSiteId);

    // A submitted update to the ORS, changing nothing until configured, with its own work item.
    private void SeedUpdateRequest(Action<ChangeRequestSite> configure, int siteId = GdanskSiteId)
    {
        var site = UpdateSite(siteId);
        var previous = OverseasSiteDetails.From(site);
        var item = new ChangeRequestSite
        {
            ChangeType = AccreditationChangeRequest.UpdateOverseasSiteChangeType,
            SiteId = siteId,
            OrsId = site.OrsId,
            SiteName = site.SiteName,
            Status = ChangeItemStatus.DulyMade,
            CaseManagementWorkItemId = _workItemId,
            CaseManagementStatusUpdatedAt = Watermark,
            Previous = previous,
            Proposed = previous,
        };
        configure(item);
        _factory.FakeChangeRequests.Seed(
            new AccreditationChangeRequest
            {
                ChangeType = AccreditationChangeRequest.UpdateOverseasSiteChangeType,
                OrganisationId = UpdateOrg,
                ApplicationId = UpdateAppId,
                MaterialType = UpdateLegacy().MaterialType,
                Year = Year,
                Status = ChangeRequestStatus.Submitted,
                SubmittedAt = SubmittedAt,
                Sites = [item],
            }
        );
    }

    private static InterimSiteDetails Renamed(InterimSiteModel interim, string siteName) =>
        InterimSiteDetails.From(interim) with { SiteName = siteName };

    private static InterimSiteDetails NewInterim(string siteName) =>
        new()
        {
            Country = "Poland",
            SiteName = siteName,
            AddressLine1 = "9 Dock Road",
            TownOrCity = "Sopot",
            ContactName = "Jo Bloggs",
            ContactEmail = "jo@example.com",
            ContactPhone = "0987654321",
            OperationCodes = ["R13"],
        };

    private Task<HttpResponseMessage> PushUpdateApproved(DateTime? occurredAt = null) =>
        Push("approved", occurredAt ?? DecidedAt, effectiveDate: null);

    // Only what the update changed: a detail changed on the ORS since the update was submitted, which the update left
    // alone, keeps its newer value. The ORS stays accredited as it was.
    [Fact]
    public async Task ApprovedUpdate_WithoutAnEffectiveDate_AppliesOnlyTheChangedDetails()
    {
        SeedUpdateRequest(item =>
            item.Proposed = item.Previous! with { SiteName = "Gdansk Recykling Nowy", ContactEmail = "new@example.com" }
        );
        var site = StoredSite();
        var (selected, validFrom) = (site.Selected, site.ValidFrom);
        site.ContactPhone = "+48 111 222 333";
        site.ContactName = "Changed Since";

        var response = await PushUpdateApproved();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = UpdateSite();
        after.SiteName.Should().Be("Gdansk Recykling Nowy");
        after.ContactEmail.Should().Be("new@example.com");
        after.ContactPhone.Should().Be("+48 111 222 333");
        after.ContactName.Should().Be("Changed Since");
        after.Selected.Should().Be(selected);
        after.ValidFrom.Should().Be(validFrom);
        Item().Status.Should().Be(ChangeItemStatus.Approved);
        Item().ValidFrom.Should().Be(DecidedOn);
        Item().CaseManagementStatusUpdatedAt.Should().Be(DecidedAt);
    }

    [Fact]
    public async Task ApprovedUpdate_ChangedAddress_RebuildsTheSitesAddress()
    {
        SeedUpdateRequest(item =>
            item.Proposed = item.Previous! with { AddressLine1 = "12 New Quay", TownOrCity = "Gdansk" }
        );

        (await PushUpdateApproved()).StatusCode.Should().Be(HttpStatusCode.OK);

        UpdateSite().SiteAddress.Should().Be("12 New Quay, Gdansk, Poland");
    }

    // AC6 is for an add only: an add approved without its date is still refused.
    [Fact]
    public async Task ApprovedAdd_WithoutAnEffectiveDate_IsStill400()
    {
        SeedSubmittedRequest(ChangeItemStatus.DulyMade, Watermark);

        var response = await Push("approved", Watermark.AddMinutes(5), effectiveDate: null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    [Fact]
    public async Task ApprovedUpdate_ChangesAddsAndRemovesInterimSites()
    {
        SeedUpdateRequest(item =>
        {
            var site = UpdateSite();
            item.InterimSiteChanges =
            [
                new InterimSiteChange
                {
                    Action = InterimSiteChangeAction.Change,
                    InterimSiteId = 902511,
                    Previous = InterimSiteDetails.From(InterimOf(site, 902511)),
                    Proposed = Renamed(InterimOf(site, 902511), "Gdynia Hub Renamed"),
                },
                new InterimSiteChange
                {
                    Action = InterimSiteChangeAction.Remove,
                    InterimSiteId = 902512,
                    Previous = InterimSiteDetails.From(InterimOf(site, 902512)),
                },
                new InterimSiteChange { Action = InterimSiteChangeAction.Add, Proposed = NewInterim("Sopot Yard") },
            ];
        });

        (await PushUpdateApproved()).StatusCode.Should().Be(HttpStatusCode.OK);

        var site = UpdateSite();
        InterimOf(site, 902511).SiteName.Should().Be("Gdynia Hub Renamed");
        InterimOf(site, 902511).RemovedAt.Should().BeNull();
        var removed = InterimOf(site, 902512);
        removed.RemovedAt.Should().Be(DecidedAt);
        removed.StatusHistory.Select(h => h.Status).Should().Equal("approved", "withdrawn");
        removed.StatusHistory[^1].RecordedAt.Should().Be(DecidedAt);
        // A new SiteId after every site and interim site the application holds, and the registration's next number.
        var added = site.InterimSites.Should().ContainSingle(i => i.SiteName == "Sopot Yard").Subject;
        added.SiteId.Should().Be(902513);
        added.SiteNumber.Should().Be("003");
        added.IsNewSite.Should().BeTrue();
        added.CreatedAt.Should().Be(DecidedAt);
        added.StatusHistory.Should().ContainSingle(h => h.Status == "approved" && h.RecordedAt == DecidedAt);
        site.InterimSite!.SiteId.Should().Be(902511);
        // The id is recorded on the change, so the grant run again finds the interim site rather than adding it.
        Item()
            .InterimSiteChanges.Single(c => c.Action == InterimSiteChangeAction.Add)
            .InterimSiteId.Should()
            .Be(902513);
    }

    // The interim site a change was for has been withdrawn since: nothing to change, and a retry would fail the same
    // way, so a 409 with nothing written.
    [Fact]
    public async Task ApprovedUpdate_InterimSiteGone_Returns409AndWritesNothing()
    {
        SeedUpdateRequest(item =>
        {
            var interim = InterimOf(UpdateSite(), 902512);
            item.Proposed = item.Previous! with { SiteName = "Not applied" };
            item.InterimSiteChanges =
            [
                new InterimSiteChange
                {
                    Action = InterimSiteChangeAction.Change,
                    InterimSiteId = 902512,
                    Previous = InterimSiteDetails.From(interim),
                    Proposed = Renamed(interim, "Not applied either"),
                },
            ];
        });
        InterimOf(StoredSite(), 902512).RemovedAt = Watermark.AddDays(-3);
        var versionBefore = UpdateLegacy().Version;
        var nameBefore = UpdateSite().SiteName;

        var response = await PushUpdateApproved();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Item().Status.Should().Be(ChangeItemStatus.DulyMade);
        UpdateSite().SiteName.Should().Be(nameBefore);
        UpdateLegacy().Version.Should().Be(versionBefore);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // An ORS that is not accredited has no update to grant.
    [Fact]
    public async Task ApprovedUpdate_OrsNotAccredited_Returns409AndWritesNothing()
    {
        SeedUpdateRequest(item => item.Proposed = item.Previous! with { SiteName = "Ghent Renamed" }, siteId: 902504);
        var versionBefore = UpdateLegacy().Version;

        var response = await PushUpdateApproved();

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        UpdateSite(902504).SiteName.Should().Be("Ghent Recycling NV");
        UpdateLegacy().Version.Should().Be(versionBefore);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(0);
    }

    // New BES files join the ORS's own, as an add's do: a file the ORS already holds is not added again.
    [Fact]
    public async Task ApprovedUpdate_AppendsTheNewBesEvidence()
    {
        var site = StoredSite();
        site.BesEvidence = new BesEvidenceModel { BesEvidenceUploads = [BesFile("bes-held")] };
        SeedUpdateRequest(item =>
            item.BesEvidence = new BesEvidenceModel { BesEvidenceUploads = [BesFile("bes-held"), BesFile("bes-new")] }
        );

        (await PushUpdateApproved()).StatusCode.Should().Be(HttpStatusCode.OK);

        UpdateSite().BesEvidence!.BesEvidenceUploads.Select(f => f.FileId).Should().Equal("bes-held", "bes-new");
    }

    // A repeat of the approval, without a date as every update approval is, is answered at the final-status guard.
    [Fact]
    public async Task ApprovedUpdateAgain_WritesNothing()
    {
        SeedUpdateRequest(item =>
        {
            item.Proposed = item.Previous! with { SiteName = "Gdansk Again" };
            item.InterimSiteChanges =
            [
                new InterimSiteChange { Action = InterimSiteChangeAction.Add, Proposed = NewInterim("Sopot Yard") },
            ];
        });
        (await PushUpdateApproved()).StatusCode.Should().Be(HttpStatusCode.OK);
        var granted = Item();
        var updates = _factory.FakeChangeRequests.UpdateCount;
        var legacyVersion = UpdateLegacy().Version;

        (await PushUpdateApproved(DecidedAt.AddMinutes(5))).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Should().BeEquivalentTo(granted);
        _factory.FakeChangeRequests.UpdateCount.Should().Be(updates);
        UpdateLegacy().Version.Should().Be(legacyVersion);
        UpdateSite().InterimSites.Count(i => i.SiteName == "Sopot Yard").Should().Be(1);
    }

    // Without dual-write there is no transaction: a lost race on the item after the legacy write is read again, and
    // the grant run again adds the interim site once.
    [Fact]
    public async Task ApprovedUpdate_LostRaceOnTheItem_AddsTheInterimSiteOnce()
    {
        SeedUpdateRequest(item =>
            item.InterimSiteChanges =
            [
                new InterimSiteChange { Action = InterimSiteChangeAction.Add, Proposed = NewInterim("Sopot Yard") },
            ]
        );
        var lost = 0;
        _factory.FakeChangeRequests.LoseUpdateWhen = _ => Interlocked.Increment(ref lost) == 1;
        var versionBefore = UpdateLegacy().Version;

        (await PushUpdateApproved()).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.Approved);
        var added = UpdateSite().InterimSites.Should().ContainSingle(i => i.SiteName == "Sopot Yard").Subject;
        Item().InterimSiteChanges.Single().InterimSiteId.Should().Be(added.SiteId);
        UpdateLegacy().Version.Should().Be(versionBefore + 1);
    }

    // A refused update changes nothing on the ORS, and frees it for a new update (Rejected is not an active item).
    [Fact]
    public async Task RejectedUpdate_LeavesTheOrsUnchangedAndReleasesItsLock()
    {
        SeedUpdateRequest(item => item.Proposed = item.Previous! with { SiteName = "Never Applied" });
        var nameBefore = UpdateSite().SiteName;
        var versionBefore = UpdateLegacy().Version;
        var updateSites = $"/api/v1/accreditation-changes/{UpdateOrg}/{UpdateAppId}/requests/current/sites";
        var startUpdate = new { siteId = GdanskSiteId, changeType = "update-overseas-site" };
        (await _client.PostAsJsonAsync(updateSites, startUpdate, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await Push("rejected", DecidedAt)).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.Rejected);
        Item().ValidFrom.Should().BeNull();
        UpdateSite().SiteName.Should().Be(nameBefore);
        UpdateLegacy().Version.Should().Be(versionBefore);
        (await _client.PostAsJsonAsync(updateSites, startUpdate, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ---------------------------------------------------------------- flag and auth

    private static bool IsMapped(AccreditationChangeTestFactory factory) =>
        factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Any(e => e.RoutePattern.RawText == ChangeRequestStatusEndpoints.Route);

    // Off, the route is not mapped at all (with it on, an unknown work item is a 404 too, so the 404 alone proves
    // nothing).
    [Fact]
    public async Task InYearChangesFlagOff_RouteIsNotMapped()
    {
        using var factory = new AccreditationChangeTestFactory(inYearChangesEnabled: false);
        using var client = factory.CreateClient();
        IsMapped(_factory).Should().BeTrue();

        var response = await client.PostAsJsonAsync(
            StatusUrl(_workItemId),
            new { toStateId = "duly-made", actionId = "duly-make", occurredAt = Watermark },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        IsMapped(factory).Should().BeFalse();
    }

    // The same caller identity as the application status route: ManagementBe's signed CaseManagement scheme, never
    // the operator frontend's.
    [Fact]
    public void Route_RequiresTheCaseManagementScheme()
    {
        var route = _factory
            .Services.GetRequiredService<EndpointDataSource>()
            .Endpoints.OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == ChangeRequestStatusEndpoints.Route);

        var policy = route.Metadata.GetMetadata<AuthorizationPolicy>();
        policy!.AuthenticationSchemes.Should().Equal(CaseManagementAuthenticationHandler.SchemeName);
        policy.Requirements.Should().ContainSingle(r => r is DenyAnonymousAuthorizationRequirement);
    }
}
