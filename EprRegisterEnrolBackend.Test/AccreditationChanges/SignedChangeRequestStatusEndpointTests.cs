using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.Config;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-495: the change request status route outside Development, where CaseManagementAuthenticationHandler checks
// ManagementBe's v3 signature rather than trusting its headers (see SignedRecyclingOperationsEndpointTests).
public class SignedChangeRequestStatusEndpointTests
    : IClassFixture<SignedChangeRequestStatusEndpointTests.Factory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Factory _factory;
    private readonly HttpClient _client;

    public SignedChangeRequestStatusEndpointTests(Factory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Guid SeedItem()
    {
        var workItemId = Guid.NewGuid();
        _factory.FakeChangeRequests.Seed(
            new AccreditationChangeRequest
            {
                OrganisationId = "org-1",
                ApplicationId = $"app-{workItemId}",
                MaterialType = MaterialType.Plastic,
                Year = 2026,
                Status = ChangeRequestStatus.Submitted,
                Sites =
                [
                    new ChangeRequestSite
                    {
                        SiteId = 1,
                        SiteName = "Site 1",
                        Status = ChangeItemStatus.Submitted,
                        CaseManagementWorkItemId = workItemId,
                    },
                ],
            }
        );
        return workItemId;
    }

    private static HttpRequestMessage Push(Guid workItemId, bool signed)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/" + ChangeRequestStatusEndpoints.Route.Replace("{workItemId}", workItemId.ToString())
        )
        {
            Content = JsonContent.Create(
                new { toStateId = "duly-made", actionId = "duly-make", occurredAt = DateTime.UtcNow }
            ),
        };
        if (!signed)
            return request;

        const string userId = "regulator@example.gov.uk";
        const string userName = "Jane Regulator";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        request.Headers.Add("x-cdp-client-id", SignedCaseManagementTestFactory.ClientId);
        request.Headers.Add("x-cdp-user-id", userId);
        request.Headers.Add("x-cdp-user-name", userName);
        request.Headers.Add(
            "x-cdp-auth-signature",
            CaseManagementAuthenticationHandler.ComputeSignature(
                SignedCaseManagementTestFactory.SharedSecret,
                SignedCaseManagementTestFactory.ClientId,
                userId,
                userName,
                timestamp,
                nonce
            )
        );
        request.Headers.Add("x-cdp-auth-timestamp", timestamp);
        request.Headers.Add("x-cdp-auth-nonce", nonce);
        return request;
    }

    private ChangeItemStatus StatusOf(Guid workItemId) =>
        _factory
            .FakeChangeRequests.All.SelectMany(r => r.Sites)
            .Single(s => s.CaseManagementWorkItemId == workItemId)
            .Status;

    [Fact]
    public async Task UnsignedPush_Returns401AndWritesNothing()
    {
        var workItemId = SeedItem();

        var response = await _client.SendAsync(Push(workItemId, signed: false), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        StatusOf(workItemId).Should().Be(ChangeItemStatus.Submitted);
    }

    [Fact]
    public async Task OperatorFrontendBearerToken_Returns401()
    {
        var workItemId = SeedItem();
        var request = Push(workItemId, signed: false);
        request.Headers.Authorization = new("Bearer", Factory.FrontendSecret);

        var response = await _client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        StatusOf(workItemId).Should().Be(ChangeItemStatus.Submitted);
    }

    [Fact]
    public async Task SignedPush_IsApplied()
    {
        var workItemId = SeedItem();

        var response = await _client.SendAsync(Push(workItemId, signed: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        StatusOf(workItemId).Should().Be(ChangeItemStatus.DulyMade);
    }

    // The in-year changes flag outside Development needs dual-write on (FeatureFlags.EnsureInYearChangesHasDualWrite),
    // and dual-write needs expand. Neither is reached by this route.
    public class Factory : SignedCaseManagementTestFactory
    {
        public const string FrontendSecret = "ra-495-frontend-secret";

        public FakeChangeRequestPersistence FakeChangeRequests { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting(FeatureFlags.InYearChangesEnabledKey, "true");
            builder.UseSetting("DomainModel:DualWrite", "true");
            builder.UseSetting("DomainModel:ExpandEnabled", "true");
            builder.UseSetting("AUTH_SHARED_SECRET:FRONTEND", FrontendSecret);
            builder.ConfigureServices(services =>
                services.AddSingleton<IChangeRequestPersistence>(FakeChangeRequests)
            );
        }
    }
}
