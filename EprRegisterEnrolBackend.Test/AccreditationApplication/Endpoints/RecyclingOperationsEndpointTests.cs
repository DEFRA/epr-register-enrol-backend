using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-469 AC18: PATCH .../overseas-sites/{siteId}/recycling-operations, authorized for
// management-be's CaseManagement client credentials (not FrontendOnly) - same shape as
// AccreditationNumberEndpointTests.cs's split-out file for the other CaseManagement-scheme
// routes, kept out of the (already very large) main AccreditationApplicationEndpointsTests.cs.
// Does not exercise audit persistence (epr-register-enrol-backend-9kr's scope) - the factory
// here has no IRecyclingOperationsAuditPersistence registered, matching that this endpoint's own
// scope is the site-mutation mechanics only.
public class RecyclingOperationsEndpointTests : IClassFixture<AccreditationApplicationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public RecyclingOperationsEndpointTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private void Reset()
    {
        _factory.FakePersistence.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCdpUploaderService.ClearSubstitute(ClearOptions.All);
    }

    // Steel's applicable codes (RecyclingOperationCodes.CodesByMaterialType) are R4/R12/R13 -
    // matches the main test file's own SeedApplication default MaterialType.
    private AccreditationApplicationModel SeedApplication(
        string orgId = "org-123",
        ApplicationStatus status = ApplicationStatus.Saved,
        MaterialType materialType = MaterialType.Steel,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = orgId,
            Year = 2026,
            MaterialType = materialType,
            ApplicationStatus = status,
        };
        configure?.Invoke(app);
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private AccreditationApplicationModel SeedApplicationWithOverseasSite(
        int siteId = 1,
        string siteName = "Test Site",
        InterimSiteModel? interimSite = null,
        List<string>? initialCodes = null,
        MaterialType materialType = MaterialType.Steel,
        ApplicationStatus status = ApplicationStatus.Saved
    ) =>
        SeedApplication(
            materialType: materialType,
            status: status,
            configure: a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    SectionStatus = SectionStatus.Completed,
                    Sites =
                    [
                        new OverseasSiteModel
                        {
                            SiteId = siteId,
                            SiteName = siteName,
                            InterimSite = interimSite,
                            OperationCodes = initialCodes ?? ["R4"],
                        },
                    ],
                }
        );

    private static string Url(AccreditationApplicationModel app, int siteId) =>
        $"/api/v1/accreditation-applications/{app.OrganisationId}/{app.ApplicationId}/overseas-sites/{siteId}/recycling-operations";

    [Fact]
    public async Task PatchRecyclingOperations_ValidCodes_Returns200WithOnlyTheUpdatedSite()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        body!.SiteId.Should().Be(1);
        body.OperationCodes.Should().BeEquivalentTo(["R4"]);
    }

    [Fact]
    public async Task PatchRecyclingOperations_ValidCodes_DoesNotMutateSectionStatusOrOtherApplicationFields()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();
        var originalDateLastEdited = app.DateLastEdited;

        await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        var stored = await _factory.FakePersistence.GetByIdAsync(
            app.OrganisationId,
            app.ApplicationId!
        );
        stored!.OverseasSites!.SectionStatus.Should().Be(SectionStatus.Completed);
        stored.DateLastEdited.Should().Be(originalDateLastEdited);
    }

    [Fact]
    public async Task PatchRecyclingOperations_ApplicationNotFound_Returns404()
    {
        Reset();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/overseas-sites/1/recycling-operations",
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchRecyclingOperations_UnknownSiteId_Returns404()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(siteId: 1);

        var response = await _client.PatchAsJsonAsync(
            Url(app, 999),
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(ApplicationStatus.Withdrawn)]
    [InlineData(ApplicationStatus.Approved)]
    [InlineData(ApplicationStatus.Rejected)]
    public async Task PatchRecyclingOperations_TerminalApplicationStatus_Returns409(
        ApplicationStatus status
    )
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(status: status);

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task PatchRecyclingOperations_EmptyCodes_Returns400()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string>() },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchRecyclingOperations_R12WithNullInterimSite_Returns400()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(interimSite: null);

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R4", "R12" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchRecyclingOperations_R12WithAnInterimSitePresent_Returns200()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(
            interimSite: new InterimSiteModel
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
            }
        );

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R4", "R12" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<OverseasSiteModel>(
            JsonOptions,
            TestContext.Current.CancellationToken
        );
        body!.OperationCodes.Should().BeEquivalentTo(["R4", "R12"]);
    }

    [Fact]
    public async Task PatchRecyclingOperations_CodeNotApplicableToMaterialType_Returns400()
    {
        // Steel's applicable codes are R4/R12/R13 (RecyclingOperationCodes.CodesByMaterialType) -
        // R5 is a valid code in general (RecyclingOperationCodes.AllCodes, so the request-shape
        // validator alone lets it through) but not offered for Steel.
        Reset();
        var app = SeedApplicationWithOverseasSite(materialType: MaterialType.Steel);

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R5" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
