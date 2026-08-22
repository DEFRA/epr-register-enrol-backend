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

// RA-469 AC18/AC15/AC19: PATCH .../overseas-sites/{siteId}/recycling-operations, authorized for
// management-be's CaseManagement client credentials (not FrontendOnly) - same shape as
// AccreditationNumberEndpointTests.cs's split-out file for the other CaseManagement-scheme
// routes, kept out of the (already very large) main AccreditationApplicationEndpointsTests.cs.
// Also covers epr-register-enrol-backend-9kr's audit-wiring scope, via
// AccreditationApplicationTestFactory.MockAuditPersistence - an NSubstitute mock, so these tests
// assert RecordAsync was (or wasn't) called with the right fields rather than a real Mongo write.
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
        _factory.MockAuditPersistence.ClearSubstitute(ClearOptions.All);
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

    // epr-register-enrol-backend-8h7: simulates every MaterialType to prove the server-side
    // RecyclingOperationCodes.CodesByMaterialType mapping matches epr-register-enrol-frontend's
    // CODES_BY_MATERIAL_TYPE (see RecyclingOperationCodes' own doc comment) - one non-R12/R13
    // code that IS offered for the material type (expect 200) and one that ISN'T (expect 400),
    // covering all 7 MaterialType values so a future drift in either map's per-material set
    // fails this test rather than silently diverging between the two repos.
    [Theory]
    [InlineData(MaterialType.Aluminium, "R4", "R3")]
    [InlineData(MaterialType.Fibre, "R3", "R4")]
    [InlineData(MaterialType.Glass, "R5", "R3")]
    [InlineData(MaterialType.Paper, "R3", "R4")]
    [InlineData(MaterialType.Plastic, "R3", "R4")]
    [InlineData(MaterialType.Steel, "R4", "R3")]
    [InlineData(MaterialType.Wood, "R3", "R4")]
    public async Task PatchRecyclingOperations_MaterialTypeApplicability_MatchesFrontendCodesByMaterialType(
        MaterialType materialType,
        string allowedCode,
        string disallowedCode
    )
    {
        Reset();
        var allowedApp = SeedApplicationWithOverseasSite(
            materialType: materialType,
            siteName: $"Site-{materialType}-allowed"
        );

        var allowedResponse = await _client.PatchAsJsonAsync(
            Url(allowedApp, 1),
            new { OperationCodes = new List<string> { allowedCode } },
            TestContext.Current.CancellationToken
        );

        allowedResponse
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, $"{allowedCode} should be applicable for {materialType}");

        var disallowedApp = SeedApplicationWithOverseasSite(
            materialType: materialType,
            siteName: $"Site-{materialType}-disallowed"
        );

        var disallowedResponse = await _client.PatchAsJsonAsync(
            Url(disallowedApp, 1),
            new { OperationCodes = new List<string> { disallowedCode } },
            TestContext.Current.CancellationToken
        );

        disallowedResponse
            .StatusCode.Should()
            .Be(
                HttpStatusCode.BadRequest,
                $"{disallowedCode} should not be applicable for {materialType}"
            );
    }

    // --- RA-469 AC15/AC19 (epr-register-enrol-backend-9kr): audit persistence wiring ---

    [Fact]
    public async Task PatchRecyclingOperations_SuccessfulEdit_WritesExactlyOneAuditRecordWithBeforeAndAfterCodes()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite(
            initialCodes: ["R4"],
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
        await _factory
            .MockAuditPersistence.Received(1)
            .RecordAsync(
                Arg.Is<RecyclingOperationsAuditRecord>(r =>
                    r.OrganisationId == app.OrganisationId
                    && r.ApplicationId == app.ApplicationId
                    && r.SiteId == 1
                    && r.BeforeCodes.SequenceEqual(new[] { "R4" })
                    && r.AfterCodes.SequenceEqual(new[] { "R4", "R12" })
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task PatchRecyclingOperations_IdentityClaimsAbsentFromDevBypass_DoesNotThrowAndRecordsEmptyActor()
    {
        // AccreditationApplicationTestFactory runs Development with no CaseManagement shared
        // secret configured, so CaseManagementAuthenticationHandler's dev-bypass authenticates
        // with a zero-claim principal (see CaseManagementAuthenticationHandler's own
        // devPrincipal) - exactly the "claims absent" path this endpoint must not throw on.
        Reset();
        var app = SeedApplicationWithOverseasSite(initialCodes: ["R4"]);

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockAuditPersistence.Received(1)
            .RecordAsync(
                Arg.Is<RecyclingOperationsAuditRecord>(r =>
                    r.CdpUserId == string.Empty && r.CdpUserName == string.Empty
                ),
                Arg.Any<CancellationToken>()
            );
    }

    [Fact]
    public async Task PatchRecyclingOperations_ValidCodes_DoesNotMutateTheOverseasSitesVersionsSnapshot()
    {
        Reset();
        var snapshotVersionedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var app = SeedApplication(configure: a =>
            a.OverseasSites = new AccreditationApplicationOverseasSites
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
                Versions =
                [
                    new OverseasSitesSnapshot
                    {
                        VersionedAt = snapshotVersionedAt,
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
                ],
            }
        );

        await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        var stored = await _factory.FakePersistence.GetByIdAsync(
            app.OrganisationId,
            app.ApplicationId!
        );
        stored!.OverseasSites!.Versions.Should().ContainSingle();
        stored.OverseasSites.Versions[0].VersionedAt.Should().Be(snapshotVersionedAt);
    }

    [Fact]
    public async Task PatchRecyclingOperations_ValidationFailure_DoesNotWriteAnAuditRecord()
    {
        Reset();
        var app = SeedApplicationWithOverseasSite();

        var response = await _client.PatchAsJsonAsync(
            Url(app, 1),
            new { OperationCodes = new List<string>() },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await _factory
            .MockAuditPersistence.DidNotReceive()
            .RecordAsync(Arg.Any<RecyclingOperationsAuditRecord>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchRecyclingOperations_ApplicationNotFound_DoesNotWriteAnAuditRecord()
    {
        Reset();

        var response = await _client.PatchAsJsonAsync(
            $"/api/v1/accreditation-applications/org-123/{ObjectId.GenerateNewId()}/overseas-sites/1/recycling-operations",
            new { OperationCodes = new List<string> { "R4" } },
            TestContext.Current.CancellationToken
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await _factory
            .MockAuditPersistence.DidNotReceive()
            .RecordAsync(Arg.Any<RecyclingOperationsAuditRecord>(), Arg.Any<CancellationToken>());
    }
}
