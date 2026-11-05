using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.DevSeed;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ClearExtensions;
using NSubstitute.ExceptionExtensions;
using Microsoft.Extensions.DependencyInjection;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-492: in-year change request to add existing registered ORSs to a granted accreditation.
// The approved application is DevExporterAccreditationSeedService's org 50019 fixture: 901901
// accredited, 901910 registered and complete (France), 901911 registered with only a country
// (India, so BES evidence is needed unless conditions of export are met), 901912 registered
// missing R code and conditions of export (Turkey, OECD, so no BES).
public sealed class AccreditationChangeEndpointsTests : IDisposable
{
    private const int Year = 2026;
    private static readonly string Org = DevExporterOrg.InYearChange.OrganisationId;
    private static readonly string AppId = DevExporterAccreditationSeedService.ApplicationIdFor(DevExporterOrg.InYearChange, Year).ToString();
    private static readonly string AccreditationReference = DevExporterOrg.InYearChange.AccreditationNumberFor(Year);
    private static readonly string Base = $"/api/v1/accreditation-changes/{Org}/{AppId}";
    private static readonly string Sites = $"{Base}/requests/current/sites";
    private static readonly string SubmitUrl = $"{Base}/requests/current/submit";

    private static readonly string[] R3 = ["R3"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AccreditationChangeTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationChangeEndpointsTests()
    {
        _factory = new AccreditationChangeTestFactory(submitToCaseManagement: false);
        _factory.FakePersistence.Seed(DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object Declaration => new { fullName = "Jane Smith", jobTitle = "Director" };

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private Task<HttpResponseMessage> AddSite(int siteId) =>
        _client.PostAsJsonAsync(Sites, new { siteId }, Ct);

    private Task<HttpResponseMessage> Patch(int siteId, object body) =>
        _client.PatchAsJsonAsync($"{Sites}/{siteId}", body, Ct);

    private async Task<JsonElement> Get()
    {
        var response = await _client.GetAsync(Base, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJson(response);
    }

    private static JsonElement SiteIn(JsonElement request, int siteId) =>
        request.GetProperty("sites").EnumerateArray().Single(s => s.GetProperty("siteId").GetInt32() == siteId);

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToList();

    private async Task SubmitCompleteRequestFor901910()
    {
        (await AddSite(901910)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.PostAsJsonAsync(SubmitUrl, Declaration, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string> SeedCleanUpload(string fileId, string filename)
    {
        var fileUploadId = Guid.NewGuid().ToString();
        await _factory.FakePendingUploadService.CreateAsync(fileUploadId, "http://cdp/status", ct: Ct);
        await _factory.FakePendingUploadService.CompleteAsync(
            fileUploadId,
            new CdpCallbackFile
            {
                FileId = fileId,
                Filename = filename,
                FileStatus = "complete",
                ContentType = "application/pdf",
                S3Key = $"bes-evidence/{fileId}",
                S3Bucket = "bes-evidence",
            },
            Ct
        );
        return fileUploadId;
    }

    // ---------------------------------------------------------------- read / accreditation guards

    [Fact]
    public async Task Get_ListsEverySiteWithItsAccreditationStatusAndLockedFields()
    {
        var view = await Get();

        view.GetProperty("accreditationReference").GetString().Should().Be(AccreditationReference);
        var sites = view.GetProperty("sites").EnumerateArray().ToList();
        sites.Select(s => s.GetProperty("accreditationStatus").GetString())
            .Should()
            .Equal("Accredited", "Registered", "Registered", "Registered");
        Strings(SiteIn(view, 901911).GetProperty("lockedFields")).Should().Equal("country");
        Strings(SiteIn(view, 901910).GetProperty("lockedFields"))
            .Should()
            .Equal("country", "coordinates", "operationCodes", "conditionsOfExport");
        SiteIn(view, 901911).GetProperty("site").GetProperty("orsId").GetString().Should().Be("006");
        view.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);
        view.GetProperty("pendingOrsIds").GetArrayLength().Should().Be(0);
    }

    // Off, the routes are not mapped at all, as RA-425's registration status endpoint.
    [Fact]
    public async Task InYearChangesFlagOff_EveryRouteReturns404()
    {
        using var factory = new AccreditationChangeTestFactory(inYearChangesEnabled: false);
        factory.FakePersistence.Seed(DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year));
        using var client = factory.CreateClient();

        (await client.GetAsync(Base, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(Sites, new { siteId = 901910 }, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync(SubmitUrl, Declaration, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.FakeChangeRequests.All.Should().BeEmpty();
        factory.Services.GetService<IChangeRequestPersistence>().Should().BeNull();
    }

    [Fact]
    public async Task Get_UnknownApplication_Returns404()
    {
        var response = await _client.GetAsync(
            $"/api/v1/accreditation-changes/{Org}/{MongoDB.Bson.ObjectId.GenerateNewId()}",
            Ct
        );
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync($"/api/v1/accreditation-changes/{Org}/not-an-id", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(ApplicationStatus.Submitted, true)]
    [InlineData(ApplicationStatus.Withdrawn, true)]
    [InlineData(ApplicationStatus.Approved, false)]
    public async Task Get_AccreditationNotGrantedOrNotExporter_Returns409(
        ApplicationStatus status,
        bool isExporter
    )
    {
        var other = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year);
        other.Id = MongoDB.Bson.ObjectId.GenerateNewId();
        other.ApplicationStatus = status;
        other.IsExporter = isExporter;
        _factory.FakePersistence.Seed(other);

        var response = await _client.GetAsync($"/api/v1/accreditation-changes/{Org}/{other.Id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- AC01 / AC10 add sites

    [Fact]
    public async Task AddSite_RegisteredSite_CreatesDraftThatReusesTheExistingOrsAndLeavesTheAccreditationUntouched()
    {
        var response = await AddSite(901911);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var request = await ReadJson(response);
        request.GetProperty("status").GetString().Should().Be("InProgress");
        request.GetProperty("changeType").GetString().Should().Be("add-overseas-site");
        var site = SiteIn(request, 901911);
        site.GetProperty("orsId").GetString().Should().Be("006");
        site.GetProperty("siteName").GetString().Should().Be("Delhi Polymers Pvt");

        var stored = await _factory.FakePersistence.GetByIdAsync(Org, AppId);
        stored!.OverseasSites!.Sites.Should().HaveCount(4);
        stored.OverseasSites.Sites.Select(s => s.OrsId).Should().Equal("001", "005", "006", "007");
        stored.OverseasSites.Sites.Single(s => s.SiteId == 901911).Selected.Should().BeFalse();
    }

    [Fact]
    public async Task AddSite_AnotherSite_AddsItToTheSameDraftAndChargesPerSite()
    {
        var first = await ReadJson(await AddSite(901911));
        var second = await ReadJson(await AddSite(901912));

        second.GetProperty("requestId").GetString().Should().Be(first.GetProperty("requestId").GetString());
        second.GetProperty("sites").GetArrayLength().Should().Be(2);
        var charge = second.GetProperty("charge");
        charge.GetProperty("chargeableSites").GetInt32().Should().Be(2);
        charge.GetProperty("chargePerSitePence").GetInt32().Should().Be(32800);
        charge.GetProperty("totalPence").GetInt32().Should().Be(65600);
        _factory.FakeChangeRequests.All.Should().ContainSingle();
    }

    [Fact]
    public async Task AddSite_SiteAlreadyInDraft_Returns200WithoutDuplicatingIt()
    {
        await AddSite(901911);

        var response = await AddSite(901911);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(response)).GetProperty("sites").GetArrayLength().Should().Be(1);
    }

    // A site added on a reaccreditation application and never registered is on neither of RA-425's
    // tabs: the view leaves it out and adding it is a 404, as for an unknown site.
    [Fact]
    public async Task NeverRegisteredSite_IsNotListedAndCannotBeAdded()
    {
        var accreditation = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year);
        accreditation.Id = MongoDB.Bson.ObjectId.GenerateNewId();
        accreditation.OverseasSites!.Sites.Add(
            new OverseasSiteModel { SiteId = 901999, OrsId = "008", SiteName = "New Site", Country = "Spain", IsNewSite = true, Selected = false }
        );
        _factory.FakePersistence.Seed(accreditation);
        var baseUrl = $"/api/v1/accreditation-changes/{Org}/{accreditation.Id}";

        var view = await ReadJson(await _client.GetAsync(baseUrl, Ct));
        view.GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("siteId").GetInt32())
            .Should()
            .Equal(901901, 901910, 901911, 901912);
        (await _client.PostAsJsonAsync($"{baseUrl}/requests/current/sites", new { siteId = 901999 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        _factory.FakeChangeRequests.All.Should().BeEmpty();
    }

    [Fact]
    public async Task AddSite_AccreditedSite_Returns409()
    {
        (await AddSite(901901)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.FakeChangeRequests.All.Should().BeEmpty();
    }

    [Fact]
    public async Task AddSite_UnknownSite_Returns404()
    {
        (await AddSite(123)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- AC02 completeness / locked fields

    [Fact]
    public async Task Draft_IdentifiesEveryMissingFieldPerSite()
    {
        await AddSite(901911);
        await AddSite(901912);

        var draft = (await Get()).GetProperty("activeRequest");

        Strings(SiteIn(draft, 901911).GetProperty("missingFields"))
            .Should()
            .Equal("coordinates", "operationCodes", "conditionsOfExport", "besEvidence");
        Strings(SiteIn(draft, 901912).GetProperty("missingFields"))
            .Should()
            .Equal("operationCodes", "conditionsOfExport");
        draft.GetProperty("isComplete").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task PatchSite_LockedField_Returns400AndChangesNothing()
    {
        await AddSite(901911);

        var response = await Patch(901911, new { country = "Sri Lanka", coordinates = "13.0827, 80.2707" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var errors = (await ReadJson(response)).GetProperty("errors");
        errors.TryGetProperty("country", out _).Should().BeTrue();
        errors.TryGetProperty("coordinates", out _).Should().BeFalse();
        _factory.FakeChangeRequests.All.Single().Sites.Single().Answers.Coordinates.Should().BeNull();
    }

    [Theory]
    [InlineData("{\"coordinates\":\"north-ish\"}", "coordinates")]
    [InlineData("{\"operationCodes\":[\"R99\"]}", "operationCodes")]
    [InlineData("{\"operationCodes\":[\"R12\"]}", "operationCodes")]
    [InlineData("{\"country\":\"\"}", "country")]
    public async Task PatchSite_InvalidValue_Returns400(string body, string field)
    {
        await AddSite(901911);

        var response = await _client.PatchAsync(
            $"{Sites}/901911",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJson(response)).GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task PatchSite_SuppliesMissingFields_CompletesTheSite()
    {
        await AddSite(901912);

        var response = await Patch(901912, new { operationCodes = R3, conditionsOfExport = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = SiteIn(await ReadJson(response), 901912);
        site.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        site.GetProperty("besEvidenceRequired").GetBoolean().Should().BeFalse();
        site.GetProperty("effective").GetProperty("country").GetString().Should().Be("Turkey");
        Strings(site.GetProperty("answers").GetProperty("operationCodes")).Should().Equal("R3");
    }

    [Fact]
    public async Task PatchSite_NotInDraft_Returns404()
    {
        (await Patch(901912, new { conditionsOfExport = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await AddSite(901911);
        (await Patch(901912, new { conditionsOfExport = true })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- BES evidence

    // France is in the EU and OECD; India stops needing evidence once its conditions of export are
    // met, judged on the effective values (the ORS's own or the operator's answers).
    [Theory]
    [InlineData(901910, false)]
    [InlineData(901911, true)]
    public async Task BesEvidence_SiteThatDoesNotNeedIt_Returns422AndAttachesNothing(int siteId, bool conditionsMet)
    {
        await AddSite(siteId);
        if (conditionsMet)
            await Patch(siteId, new { conditionsOfExport = true });
        var fileUploadId = await SeedCleanUpload("file-1", "bes.pdf");

        var response = await _client.PostAsJsonAsync(
            $"{Sites}/{siteId}/bes-evidence/files",
            new { fileUploadId, besEvidenceExpiryDate = "2027-12-31" },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _factory.FakeChangeRequests.All.Single().Sites.Single().BesEvidence.Should().BeNull();
    }

    [Fact]
    public async Task BesEvidence_CleanUploadSatisfiesTheRequirementAndRemovingItRestoresIt()
    {
        await AddSite(901911);
        await Patch(901911, new { coordinates = "13.0827, 80.2707", operationCodes = R3, conditionsOfExport = false });
        var fileUploadId = await SeedCleanUpload("file-1", "bes.pdf");

        var added = await _client.PostAsJsonAsync(
            $"{Sites}/901911/bes-evidence/files",
            new { fileUploadId, besEvidenceExpiryDate = "2027-12-31" },
            Ct
        );

        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = SiteIn(await ReadJson(added), 901911);
        site.GetProperty("isComplete").GetBoolean().Should().BeTrue();
        site.GetProperty("besEvidence").GetProperty("besEvidenceUploads")[0]
            .GetProperty("scanStatus").GetString().Should().Be("Clean");

        var removed = await _client.DeleteAsync($"{Sites}/901911/bes-evidence/files/file-1", Ct);

        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        Strings(SiteIn(await ReadJson(removed), 901911).GetProperty("missingFields")).Should().Equal("besEvidence");
        (await _client.DeleteAsync($"{Sites}/901911/bes-evidence/files/file-1", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task BesEvidence_ConditionsOfExportMet_NoEvidenceNeeded()
    {
        await AddSite(901911);

        var response = await Patch(901911, new { coordinates = "13.0827, 80.2707", operationCodes = R3, conditionsOfExport = true });

        var site = SiteIn(await ReadJson(response), 901911);
        site.GetProperty("besEvidenceRequired").GetBoolean().Should().BeFalse();
        site.GetProperty("isComplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task BesEvidence_UnscannedUploadOrDuplicateFilename_Returns422()
    {
        await AddSite(901911);
        await AddSite(901912);
        var pending = Guid.NewGuid().ToString();
        await _factory.FakePendingUploadService.CreateAsync(pending, "http://cdp/status", ct: Ct);

        (await _client.PostAsJsonAsync($"{Sites}/901911/bes-evidence/files", new { fileUploadId = pending }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await _client.PostAsJsonAsync(
            $"{Sites}/901911/bes-evidence/files",
            new { fileUploadId = await SeedCleanUpload("file-1", "same.pdf") },
            Ct
        );
        (await _client.PostAsJsonAsync(
            $"{Sites}/901912/bes-evidence/files",
            new { fileUploadId = await SeedCleanUpload("file-2", "SAME.pdf") },
            Ct
        )).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task InitiateBesEvidenceUpload_StartsACdpUploadInTheBesBucketForTheDraft()
    {
        var body = new { redirectUrl = "http://frontend/back", s3Bucket = "bucket", s3Path = "ors/901911" };
        (await _client.PostAsJsonAsync($"{Base}/requests/current/bes-evidence/initiate", body, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        _factory
            .MockCdpUploaderService.InitiateAsync(Arg.Any<CdpInitiateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CdpInitiateResponse { UploadId = "u1", UploadUrl = "http://cdp/upload/u1", StatusUrl = "http://cdp/status/u1" });
        await AddSite(901911);

        var response = await _client.PostAsJsonAsync($"{Base}/requests/current/bes-evidence/initiate", body, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJson(response);
        json.GetProperty("uploadUrl").GetString().Should().Be("http://cdp/upload/u1");
        json.GetProperty("statusUrl").GetString().Should().Contain($"/api/v1/accreditation-applications/{Org}/{AppId}/files/");
        await _factory.MockCdpUploaderService.Received(1).InitiateAsync(
            Arg.Is<CdpInitiateRequest>(r => r.S3Path == "bes-evidence/ors/901911"),
            Arg.Any<CancellationToken>()
        );
    }

    // ---------------------------------------------------------------- remove / cancel

    [Fact]
    public async Task RemoveSite_OneOfTwo_KeepsTheDraft_LastOne_DeletesIt()
    {
        await AddSite(901911);
        await AddSite(901912);

        var first = await _client.DeleteAsync($"{Sites}/901911", Ct);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(first)).GetProperty("sites").GetArrayLength().Should().Be(1);

        (await _client.DeleteAsync($"{Sites}/901912", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        _factory.FakeChangeRequests.All.Should().BeEmpty();
        (await _client.DeleteAsync($"{Sites}/901912", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- AC05 / AC07 / AC08 / AC09 submit

    [Fact]
    public async Task Submit_NoDraft_Returns404()
    {
        (await _client.PostAsJsonAsync(SubmitUrl, Declaration, Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Two submits race: the loser's save is refused before anything is sent anywhere.
    [Fact]
    public async Task Submit_LostRace_Returns409AndStaysInProgress()
    {
        await AddSite(901910);
        _factory.FakeChangeRequests.LoseUpdateWhen = r => r.Status == ChangeRequestStatus.Submitted;

        var response = await _client.PostAsJsonAsync(SubmitUrl, Declaration, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.InProgress);
        stored.SubmittedBy.Should().BeNull();
    }

    [Fact]
    public async Task Submit_MissingDeclarationDetails_Returns400()
    {
        await AddSite(901910);

        var response = await _client.PostAsJsonAsync(SubmitUrl, new { fullName = "", jobTitle = "" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.FakeChangeRequests.All.Single().Status.Should().Be(ChangeRequestStatus.InProgress);
    }

    [Fact]
    public async Task Submit_IncompleteSite_Returns422NamingEachFieldAndStaysInProgress()
    {
        await AddSite(901910);
        await AddSite(901912);

        var response = await _client.PostAsJsonAsync(SubmitUrl, Declaration, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var errors = (await ReadJson(response)).GetProperty("errors");
        errors.EnumerateObject().Select(p => p.Name)
            .Should()
            .BeEquivalentTo("sites[901912].operationCodes", "sites[901912].conditionsOfExport");
        _factory.FakeChangeRequests.All.Single().Status.Should().Be(ChangeRequestStatus.InProgress);
    }

    [Fact]
    public async Task Submit_Complete_MarksSubmittedWithoutCaseManagementAndKeepsTheSitePending()
    {
        await AddSite(901910);

        var response = await _client.PostAsJsonAsync(
            SubmitUrl,
            new { fullName = "Jane Smith", jobTitle = "Director", email = "jane@example.com", paymentReference = "PAY-1" },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var submitted = await ReadJson(response);
        submitted.GetProperty("status").GetString().Should().Be("Submitted");
        submitted.GetProperty("submittedBy").GetProperty("fullName").GetString().Should().Be("Jane Smith");
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.ChargePence.Should().Be(32800);
        stored.PaymentReference.Should().Be("PAY-1");
        stored.SubmittedAt.Should().NotBeNull();
        stored.CaseManagementReference.Should().BeNull();
        await _factory.MockCaseWorkingAdapter.DidNotReceiveWithAnyArgs()
            .SubmitChangeRequestAsync(default!, default!, Ct);

        // AC08: the existing registration association stays; the site is shown as pending.
        var view = await Get();
        var site = view.GetProperty("sites").EnumerateArray().Single(s => s.GetProperty("siteId").GetInt32() == 901910);
        site.GetProperty("accreditationStatus").GetString().Should().Be("Registered");
        site.GetProperty("pendingRequestStatus").GetString().Should().Be("Submitted");
        site.GetProperty("site").GetProperty("selected").GetBoolean().Should().BeFalse();
        view.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);
        Strings(view.GetProperty("pendingOrsIds")).Should().Equal("005");
    }

    [Fact]
    public async Task AddSite_SiteInASubmittedRequest_Returns409_ButOtherSitesStartANewDraft()
    {
        await SubmitCompleteRequestFor901910();

        (await AddSite(901910)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var next = await AddSite(901912);
        next.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.FakeChangeRequests.All.Should().HaveCount(2);
        Strings((await Get()).GetProperty("pendingOrsIds")).Should().BeEquivalentTo("005", "007");
    }
}

// The hand-off flag switched on.
public sealed class AccreditationChangeCaseManagementHandOffTests : IDisposable
{
    private const int Year = 2026;
    private static readonly string Org = DevExporterOrg.InYearChange.OrganisationId;
    private static readonly string AppId = DevExporterAccreditationSeedService.ApplicationIdFor(DevExporterOrg.InYearChange, Year).ToString();
    private static readonly string AccreditationReference = DevExporterOrg.InYearChange.AccreditationNumberFor(Year);
    private static readonly string Base = $"/api/v1/accreditation-changes/{Org}/{AppId}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AccreditationChangeTestFactory _factory = new(submitToCaseManagement: true);
    private readonly HttpClient _client;

    public AccreditationChangeCaseManagementHandOffTests()
    {
        _factory.FakePersistence.Seed(DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage> AddAndSubmit()
    {
        await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901910 }, Ct);
        return await _client.PostAsJsonAsync(
            $"{Base}/requests/current/submit",
            new { fullName = "Jane Smith", jobTitle = "Director" },
            Ct
        );
    }

    [Fact]
    public async Task Submit_FlagOn_SendsTheRequestToCaseManagementAndStoresItsReference()
    {
        var workItemId = Guid.NewGuid();
        _factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new CaseWorkingSubmissionResult("CHG-1", workItemId));

        var response = await AddAndSubmit();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.CaseManagementReference.Should().Be("CHG-1");
        stored.CaseManagementWorkItemId.Should().Be(workItemId);
        await _factory.MockCaseWorkingAdapter.Received(1).SubmitChangeRequestAsync(
            Arg.Is<AccreditationChangeRequest>(r =>
                r.Sites.Single().OrsId == "005" && r.ChargePence == 32800 && r.SubmittedBy!.FullName == "Jane Smith"
            ),
            Arg.Is<AccreditationApplicationModel>(a => a.AccreditationReference == AccreditationReference),
            Arg.Any<CancellationToken>()
        );
    }

    private void CaseManagementReturns(CaseWorkingSubmissionResult result) =>
        _factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(result);

    private void CaseManagementThrows(Exception exception) =>
        _factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(exception);

    // Save first: by the time Case Management is called the request is already Submitted, so a
    // concurrent submit can no longer reach Case Management for it.
    [Fact]
    public async Task Submit_FlagOn_SavesTheRequestAsSubmittedBeforeCallingCaseManagement()
    {
        ChangeRequestStatus? storedWhenCalled = null;
        _factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(_ =>
            {
                storedWhenCalled = _factory.FakeChangeRequests.All.Single().Status;
                return new CaseWorkingSubmissionResult("CHG-1", Guid.NewGuid());
            });

        (await AddAndSubmit()).StatusCode.Should().Be(HttpStatusCode.OK);

        storedWhenCalled.Should().Be(ChangeRequestStatus.Submitted);
    }

    [Fact]
    public async Task Submit_FlagOn_LostRace_Returns409AndNeverCallsCaseManagement()
    {
        CaseManagementReturns(new CaseWorkingSubmissionResult("CHG-1", Guid.NewGuid()));
        await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901910 }, Ct);
        _factory.FakeChangeRequests.LoseUpdateWhen = r => r.Status == ChangeRequestStatus.Submitted;

        var response = await _client.PostAsJsonAsync(
            $"{Base}/requests/current/submit",
            new { fullName = "Jane Smith", jobTitle = "Director" },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await _factory.MockCaseWorkingAdapter.DidNotReceiveWithAnyArgs()
            .SubmitChangeRequestAsync(default!, default!, Ct);
        _factory.FakeChangeRequests.All.Single().Status.Should().Be(ChangeRequestStatus.InProgress);
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("timeout")]
    [InlineData("not-configured")]
    public async Task Submit_FlagOn_CaseManagementFails_Returns503AndReopensTheRequest(string failure)
    {
        CaseManagementThrows(
            failure switch
            {
                "rejected" => new HttpRequestException("unknown work item type"),
                "timeout" => new CaseWorkingApiTimeoutException("timed out", new TaskCanceledException()),
                _ => new CaseWorkingNotConfiguredException(),
            }
        );

        var response = await AddAndSubmit();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.InProgress);
        stored.SubmittedAt.Should().BeNull();
        stored.SubmittedBy.Should().BeNull();
        stored.PaymentReference.Should().BeNull();
        stored.ChargePence.Should().BeNull();
        stored.CaseManagementReference.Should().BeNull();
    }

    // Reopened, the request is the draft again: its site is not pending a regulator decision, more
    // sites join it, and a retry submits it.
    [Fact]
    public async Task Submit_FlagOn_AfterACaseManagementFailure_TheDraftCanBeChangedAndResubmitted()
    {
        CaseManagementThrows(new HttpRequestException("unavailable"));
        (await AddAndSubmit()).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var view = await (await _client.GetAsync(Base, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        view.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Object);
        view.GetProperty("sites").EnumerateArray()
            .Single(s => s.GetProperty("siteId").GetInt32() == 901910)
            .GetProperty("pendingRequestStatus").GetString().Should().Be("InProgress");
        (await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901912 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
        await _client.PatchAsJsonAsync(
            $"{Base}/requests/current/sites/901912",
            new { operationCodes = new[] { "R3" }, conditionsOfExport = false },
            Ct
        );

        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        CaseManagementReturns(new CaseWorkingSubmissionResult("CHG-2", Guid.NewGuid()));
        (await _client.PostAsJsonAsync(
                $"{Base}/requests/current/submit",
                new { fullName = "Jane Smith", jobTitle = "Director" },
                Ct
            ))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.Sites.Should().HaveCount(2);
        stored.ChargePence.Should().Be(65600);
        stored.CaseManagementReference.Should().Be("CHG-2");
    }

    // Not a Case Management failure: the request is still reopened, but the error is not dressed up
    // as "try again" and goes to the exception handler.
    [Fact]
    public async Task Submit_FlagOn_UnexpectedError_ReopensTheRequestAndIsNot503()
    {
        CaseManagementThrows(new InvalidOperationException("a defect"));

        var response = await AddAndSubmit();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        _factory.FakeChangeRequests.All.Single().Status.Should().Be(ChangeRequestStatus.InProgress);
    }

    // Case Management has the work item, so the submission stands even if its reference can't be saved.
    [Fact]
    public async Task Submit_FlagOn_ReferenceNotSaved_StillSubmitted()
    {
        CaseManagementReturns(new CaseWorkingSubmissionResult("CHG-1", Guid.NewGuid()));
        _factory.FakeChangeRequests.LoseUpdateWhen = r => r.CaseManagementReference is not null;

        var response = await AddAndSubmit();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.CaseManagementReference.Should().BeNull();
    }
}
