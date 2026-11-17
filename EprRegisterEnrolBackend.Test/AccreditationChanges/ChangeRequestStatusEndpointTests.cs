using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.DevSeed;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

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
        DateTime? watermark = null
    )
    {
        var request = new AccreditationChangeRequest
        {
            OrganisationId = Org,
            ApplicationId = AppId,
            MaterialType = MaterialType.Plastic,
            Year = Year,
            Status = ChangeRequestStatus.Submitted,
            SubmittedAt = Watermark.AddDays(-1),
            Sites =
            [
                new ChangeRequestSite
                {
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

    private Task<HttpResponseMessage> Push(string toStateId, DateTime occurredAt, Guid? workItemId = null) =>
        _client.PostAsJsonAsync(
            StatusUrl(workItemId ?? _workItemId),
            new
            {
                toStateId,
                toStateDisplayName = toStateId,
                actionId = "some-action",
                actionDisplayName = "Some action",
                occurredAt,
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
    [InlineData("approved", ChangeItemStatus.Approved)]
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
