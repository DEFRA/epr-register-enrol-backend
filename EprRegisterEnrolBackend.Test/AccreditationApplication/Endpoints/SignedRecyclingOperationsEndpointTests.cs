using System.Net;
using System.Net.Http.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using EprRegisterEnrolBackend.Test.AccreditationApplication.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// epr-register-enrol-backend-8h7 (RA-469 AC15/AC19): proves actor identity flows from real
// x-cdp-user-id/x-cdp-user-name request headers, through CaseManagementAuthenticationHandler's
// real (non-dev-bypass) v3 signature verification, into the persisted audit record - everything
// else in RecyclingOperationsEndpointTests.cs runs under AccreditationApplicationTestFactory's
// Development dev-bypass (a zero-claim principal), so nothing else in the suite proves claims
// actually reach the handler via the real auth pipeline. Builds the signed request the same way
// CaseManagementAuthenticationHandlerTests.cs does (CaseManagementAuthenticationHandler.
// ComputeSignature, internal but visible via InternalsVisibleTo) and the same Production +
// AUTH_SHARED_SECRET:MANAGEMENT_BE shape as FrontendAuthenticationIntegrationTests.cs's
// ProductionFactory - but with FakePersistence/mocked adapters registered (matching
// AccreditationApplicationTestFactory) so it never needs a real Mongo instance.
public class SignedRecyclingOperationsEndpointTests : IClassFixture<SignedCaseManagementTestFactory>
{
    private readonly SignedCaseManagementTestFactory _factory;
    private readonly HttpClient _client;

    public SignedRecyclingOperationsEndpointTests(SignedCaseManagementTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private AccreditationApplicationModel SeedApplicationWithOverseasSite()
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = ApplicationStatus.Saved,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                SectionStatus = SectionStatus.Completed,
                Sites =
                [
                    new OverseasSiteModel
                    {
                        SiteId = 1,
                        SiteName = "Test Site",
                        OperationCodes = ["R4"],
                    },
                ],
            },
        };
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private HttpRequestMessage BuildSignedPatchRequest(
        AccreditationApplicationModel app,
        int siteId,
        object body,
        string userId,
        string userName
    )
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var signature = CaseManagementAuthenticationHandler.ComputeSignature(
            SignedCaseManagementTestFactory.SharedSecret,
            SignedCaseManagementTestFactory.ClientId,
            userId,
            userName,
            timestamp,
            nonce
        );

        var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/overseas-sites/{siteId}/recycling-operations"
        )
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-cdp-client-id", SignedCaseManagementTestFactory.ClientId);
        request.Headers.Add("x-cdp-user-id", userId);
        request.Headers.Add("x-cdp-user-name", userName);
        request.Headers.Add("x-cdp-auth-signature", signature);
        request.Headers.Add("x-cdp-auth-timestamp", timestamp);
        request.Headers.Add("x-cdp-auth-nonce", nonce);
        return request;
    }

    [Fact]
    public async Task PatchRecyclingOperations_ValidSignedRequest_ActorIdentityFlowsFromHeadersIntoTheAuditRecord()
    {
        _factory.MockAuditPersistence.ClearReceivedCalls();
        var app = SeedApplicationWithOverseasSite();
        var request = BuildSignedPatchRequest(
            app,
            siteId: 1,
            body: new { OperationCodes = new List<string> { "R4", "R12" } },
            userId: "regulator-42@example.gov.uk",
            userName: "Jane Regulator"
        );
        // R12 needs an associated interim site (AC11) - attach one via the seeded model directly
        // rather than a second endpoint call, keeping this test focused on the signed-request/
        // actor-identity path rather than re-proving AC11 (already covered elsewhere).
        app.OverseasSites!.Sites[0].InterimSite = new InterimSiteModel
        {
            SiteId = 2,
            SiteNumber = "SN-0002",
            Country = "France",
            SiteName = "Interim Site",
            AddressLine1 = "1 Rue Example",
            TownOrCity = "Paris",
            ContactName = "Jane Smith",
            ContactEmail = "jane.smith@example.com",
            ContactPhone = "+33 1 23 45 67 89",
        };

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockAuditPersistence.Received(1)
            .RecordAsync(
                Arg.Is<RecyclingOperationsAuditRecord>(r =>
                    r.CdpUserId == "regulator-42@example.gov.uk"
                    && r.CdpUserName == "Jane Regulator"
                    && r.OrganisationId == app.OrganisationId
                    && r.ApplicationId == app.ApplicationId
                    && r.SiteId == 1
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task PatchRecyclingOperations_InvalidSignature_Returns401AndWritesNoAuditRecord()
    {
        _factory.MockAuditPersistence.ClearReceivedCalls();
        var app = SeedApplicationWithOverseasSite();
        var request = BuildSignedPatchRequest(
            app,
            siteId: 1,
            body: new { OperationCodes = new List<string> { "R4" } },
            userId: "regulator-42@example.gov.uk",
            userName: "Jane Regulator"
        );
        request.Headers.Remove("x-cdp-auth-signature");
        request.Headers.Add("x-cdp-auth-signature", "not-the-real-signature");

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _factory
            .MockAuditPersistence.DidNotReceive()
            .RecordAsync(Arg.Any<RecyclingOperationsAuditRecord>(), Arg.Any<CancellationToken>());
    }
}

// Combines FrontendAuthenticationIntegrationTests.cs's ProductionFactory shape (real Production
// environment + a configured AUTH_SHARED_SECRET:MANAGEMENT_BE, so CaseManagementAuthenticationHandler
// runs real v3 signature verification instead of the Development header-trust bypass) with
// AccreditationApplicationTestFactory's fakes/mocks (so no real Mongo instance or CDP/ReEx/case-
// working HTTP calls are needed) - neither existing factory alone covers this combination.
public class SignedCaseManagementTestFactory : WebApplicationFactory<Program>
{
    public const string SharedSecret = "8h7-signed-request-integration-test-secret";
    public const string ClientId = "epr-register-enrol-management-be";

    public FakeAccreditationApplicationPersistence FakePersistence { get; } = new();
    public FakeRegulatoryNumberSequenceCounterPersistence FakeCounters { get; } = new();
    public IReExApiAdapter MockReExAdapter { get; } = Substitute.For<IReExApiAdapter>();
    public ICaseWorkingApiAdapter MockCaseWorkingAdapter { get; } =
        Substitute.For<ICaseWorkingApiAdapter>();
    public ICdpUploaderService MockCdpUploaderService { get; } =
        Substitute.For<ICdpUploaderService>();
    public IRecyclingOperationsAuditPersistence MockAuditPersistence { get; } =
        Substitute.For<IRecyclingOperationsAuditPersistence>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);
        builder.ConfigureAppConfiguration(
            (_, config) =>
                config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["AUTH_SHARED_SECRET:MANAGEMENT_BE"] = SharedSecret,
                        ["CaseWorking:UseStub"] = "true",
                    }
                )
        );
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IAccreditationApplicationPersistence>(FakePersistence);
            services.AddSingleton<IRegulatoryNumberSequenceCounterPersistence>(FakeCounters);
            services.AddSingleton(MockReExAdapter);
            services.AddSingleton(MockCaseWorkingAdapter);
            services.AddSingleton(MockCdpUploaderService);
            services.AddSingleton(MockAuditPersistence);
        });
    }
}
