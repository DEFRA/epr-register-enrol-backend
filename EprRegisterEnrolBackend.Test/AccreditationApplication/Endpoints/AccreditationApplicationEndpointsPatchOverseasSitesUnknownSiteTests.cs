using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-794: PATCH .../overseas-sites used to store a site it had never seen as new, with whatever
// OrsId the client sent - possibly none. That is the shape the RA-794 correction reads as a
// wrongly-defaulted ReEx site, so a direct API call could have created a genuinely new site the
// correction would then stamp false. The PATCH now only edits sites the application already holds.
public class AccreditationApplicationEndpointsPatchOverseasSitesUnknownSiteTests
    : IClassFixture<AccreditationApplicationEndpointsPatchOverseasSitesUnknownSiteTests.LoggingTestFactory>
{
    public sealed class LoggingTestFactory : AccreditationApplicationTestFactory
    {
        public CapturingLoggerFactory LogCapture { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
                services.AddSingleton<ILoggerFactory>(LogCapture)
            );
        }
    }

    private readonly LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsPatchOverseasSitesUnknownSiteTests(
        LoggingTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static OverseasSiteModel Site(int siteId, string? orsId, bool isNewSite) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = $"Site {siteId}",
            Country = "Germany",
            IsNewSite = isNewSite,
        };

    private AccreditationApplicationModel SeedApplication()
    {
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        var application = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = "org-123",
            Year = 2027,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = ApplicationStatus.Started,
            OverseasSites = new AccreditationApplicationOverseasSites
            {
                Sites = [Site(1, orsId: null, isNewSite: false), Site(2, "002", isNewSite: true)],
            },
        };
        _factory.FakePersistence.Seed(application);
        return application;
    }

    private Task<HttpResponseMessage> PatchAsync(string applicationId, object body) =>
        _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{applicationId}/overseas-sites",
            body,
            cancellationToken: TestContext.Current.CancellationToken
        );

    public static TheoryData<string?> UnknownSiteOrsIds =>
        new() { (string?)null, "", new string('9', 11), "003" };

    [Theory]
    [MemberData(nameof(UnknownSiteOrsIds))]
    public async Task Patch_UnknownSiteId_Returns400ProblemAndLogsErrorAndWritesNothing(
        string? orsId
    )
    {
        var application = SeedApplication();
        var applicationId = application.Id!.Value.ToString();
        var before = (await _factory.FakePersistence.GetByIdAsync("org-123", applicationId))!
            .OverseasSites!.Sites.Select(s => s.SiteId)
            .ToList();

        var response = await PatchAsync(
            applicationId,
            new
            {
                sites = new object[]
                {
                    new { siteId = 1, siteName = "Site 1" },
                    new { siteId = 2, siteName = "Site 2" },
                    new
                    {
                        siteId = 99,
                        orsId,
                        siteName = "Smuggled in",
                    },
                },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken
        );
        problem.GetProperty("detail").GetString().Should().Contain("99");

        var error = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Subject;
        error.Properties["ApplicationId"].Should().Be(applicationId);
        error.Properties["SiteIds"].Should().Be("99");

        (await _factory.FakePersistence.GetByIdAsync("org-123", applicationId))!
            .OverseasSites!.Sites.Select(s => s.SiteId)
            .Should()
            .Equal(before);
    }

    [Fact]
    public async Task Patch_FrontendShapeDeselectingAKnownSite_Succeeds()
    {
        // What the frontend's remove journey sends: the application's own list, one site mapped
        // to selected: false. No new site id, so nothing is rejected.
        var application = SeedApplication();
        var applicationId = application.Id!.Value.ToString();

        var response = await PatchAsync(
            applicationId,
            new
            {
                sites = new object[]
                {
                    new
                    {
                        siteId = 1,
                        siteName = "Site 1",
                        selected = false,
                    },
                    new { siteId = 2, siteName = "Site 2" },
                },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.LogCapture.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
        var stored = (await _factory.FakePersistence.GetByIdAsync("org-123", applicationId))!
            .OverseasSites!.Sites;
        stored.Single(s => s.SiteId == 1).Selected.Should().BeFalse();
        stored.Single(s => s.SiteId == 2).OrsId.Should().Be("002");
    }
}
