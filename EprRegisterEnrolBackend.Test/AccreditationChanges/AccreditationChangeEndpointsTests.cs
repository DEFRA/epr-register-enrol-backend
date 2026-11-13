using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Endpoints;
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
// accredited, 901910 and 901914 registered and complete (France; the Netherlands, with an interim
// site), 901911 registered with only a country
// (India, so BES evidence is needed unless conditions of export are met), 901912 registered
// missing R code and conditions of export (Turkey, OECD, so no BES), and 901913 never registered
// (added on the application and deselected).
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

    private static object Declaration => new { fullName = "Jane Smith", jobTitle = "Director", paymentReference = "PAY-1" };

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
            .Equal("Accredited", "Registered", "Registered", "Registered", "Registered");
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

    private string SeedOtherAccreditation(ApplicationStatus status, bool isExporter)
    {
        var other = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year);
        other.Id = MongoDB.Bson.ObjectId.GenerateNewId();
        other.ApplicationStatus = status;
        other.IsExporter = isExporter;
        _factory.FakePersistence.Seed(other);
        return $"/api/v1/accreditation-changes/{Org}/{other.Id}";
    }

    [Theory]
    [InlineData(ApplicationStatus.Submitted, true)]
    [InlineData(ApplicationStatus.Withdrawn, true)]
    [InlineData(ApplicationStatus.Submitted, false)]
    public async Task Get_AccreditationNotGranted_Returns409(ApplicationStatus status, bool isExporter)
    {
        var response = await _client.GetAsync(SeedOtherAccreditation(status, isExporter), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // RA-697: a reprocessor's change requests (tonnage band upgrades) are listed, but it has no
    // overseas sites to change, and every write stays exporter-only.
    [Fact]
    public async Task GrantedReprocessor_GetListsItsRequestsWithNoSites_ButEveryWriteReturns409()
    {
        var reprocessor = SeedOtherAccreditation(ApplicationStatus.Approved, isExporter: false);
        _factory.FakeChangeRequests.Seed(
            new AccreditationChangeRequest
            {
                OrganisationId = Org,
                ApplicationId = reprocessor.Split('/')[^1],
                MaterialType = MaterialType.Plastic,
                Year = Year,
                ChangeType = AccreditationChangeRequest.UpgradeTonnageBandChangeType,
                Status = ChangeRequestStatus.Submitted,
                TonnageBandUpgrade = new TonnageBandUpgradeItem
                {
                    Status = ChangeItemStatus.Submitted,
                    RequestedBand = PlannedTonnageBand.UpTo5000,
                },
            }
        );

        var get = await _client.GetAsync(reprocessor, Ct);

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var view = await ReadJson(get);
        view.GetProperty("sites").GetArrayLength().Should().Be(0);
        view.GetProperty("pendingOrsIds").GetArrayLength().Should().Be(0);
        view.GetProperty("requests").EnumerateArray().Single()
            .GetProperty("tonnageBandUpgrade").GetProperty("requestedBand").GetString()
            .Should().Be("UpTo5000");
        var current = $"{reprocessor}/requests/current";
        (await _client.PostAsJsonAsync($"{current}/sites", new { siteId = 901910 }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PatchAsJsonAsync($"{current}/sites/901910", new { conditionsOfExport = true }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.DeleteAsync($"{current}/sites/901910", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PostAsJsonAsync(
                $"{current}/bes-evidence/initiate",
                new { redirectUrl = "http://frontend/back", s3Bucket = "bucket", s3Path = "ors/901910" },
                Ct
            ))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PostAsJsonAsync($"{current}/submit", Declaration, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.FakeChangeRequests.All.Should().ContainSingle().Which.TonnageBandUpgrade.Should().NotBeNull();
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
        stored!.OverseasSites!.Sites.Should().HaveCount(6);
        stored.OverseasSites.Sites.Select(s => s.OrsId).Should().Equal("001", "005", "009", "006", "007", "008");
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

    // RA-697: each site on a request is an item with its own id, type and status.
    [Fact]
    public async Task AddSite_EachSiteIsAnInProgressAddOverseasSiteItemWithItsOwnId()
    {
        await AddSite(901911);
        var request = await ReadJson(await AddSite(901912));

        var items = new[] { SiteIn(request, 901911), SiteIn(request, 901912) };
        foreach (var item in items)
        {
            Guid.TryParse(item.GetProperty("itemId").GetString(), out _).Should().BeTrue();
            item.GetProperty("changeType").GetString().Should().Be("add-overseas-site");
            item.GetProperty("status").GetString().Should().Be("InProgress");
            item.GetProperty("validFrom").ValueKind.Should().Be(JsonValueKind.Null);
            item.GetProperty("withdrawnAt").ValueKind.Should().Be(JsonValueKind.Null);
        }
        items[0].GetProperty("itemId").GetString().Should().NotBe(items[1].GetProperty("itemId").GetString());
        request.GetProperty("tonnageBandUpgrade").ValueKind.Should().Be(JsonValueKind.Null);
        _factory.FakeChangeRequests.All.Single().Sites.Select(s => s.ItemId.ToString())
            .Should()
            .Equal(items.Select(i => i.GetProperty("itemId").GetString()));
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
        var view = await Get();
        view.GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("siteId").GetInt32())
            .Should()
            .Equal(901901, 901910, 901914, 901911, 901912);

        (await AddSite(901913)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        _factory.FakeChangeRequests.All.Should().BeEmpty();
    }

    // RA-739: Case Management needs every add-ORS site's ORS id, so a registered site stored without one
    // (pre-RA-507 data) is left out and cannot be added, as registration-only views leave such an ORS out.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task RegisteredSiteWithoutAnOrsId_IsNotListedAndCannotBeAdded(string? orsId)
    {
        var accreditation = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year);
        accreditation.OverseasSites!.Sites.Single(s => s.SiteId == 901912).OrsId = orsId;
        using var factory = new AccreditationChangeTestFactory(submitToCaseManagement: false);
        factory.FakePersistence.Seed(accreditation);
        using var client = factory.CreateClient();

        var get = await client.GetAsync(Base, Ct);

        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(get)).GetProperty("sites").EnumerateArray().Select(s => s.GetProperty("siteId").GetInt32())
            .Should()
            .Equal(901901, 901910, 901914, 901911);
        (await client.PostAsJsonAsync(Sites, new { siteId = 901912 }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        factory.FakeChangeRequests.All.Should().BeEmpty();
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

    // RA-605's cap, as on the application: 10 non-infected files per site; an infected one doesn't
    // count, so a user can replace it. The 422 is the application's, which the frontend recognises.
    [Theory]
    [InlineData(10, 0, HttpStatusCode.UnprocessableEntity)]
    [InlineData(9, 1, HttpStatusCode.Created)]
    [InlineData(9, 0, HttpStatusCode.Created)]
    public async Task BesEvidence_TenFileCapCountsOnlyNonInfectedFiles(int clean, int infected, HttpStatusCode expected)
    {
        await AddSite(901911);
        var draft = _factory.FakeChangeRequests.All.Single();
        draft.Sites.Single().BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads = Enumerable
                .Range(1, clean + infected)
                .Select(i => new BesEvidenceFileModel
                {
                    FileId = $"existing-{i}",
                    Filename = $"existing-{i}.pdf",
                    ScanStatus = i <= clean ? "Clean" : "Infected",
                    S3Key = $"bes-evidence/existing-{i}",
                })
                .ToList(),
        };
        (await _factory.FakeChangeRequests.UpdateAsync(draft, Ct)).Should().BeTrue();
        var fileUploadId = await SeedCleanUpload("file-new", "bes.pdf");

        var response = await _client.PostAsJsonAsync(
            $"{Sites}/901911/bes-evidence/files",
            new { fileUploadId, besEvidenceExpiryDate = "2027-12-31" },
            Ct
        );

        response.StatusCode.Should().Be(expected);
        var uploads = _factory.FakeChangeRequests.All.Single().Sites.Single().BesEvidence!.BesEvidenceUploads;
        if (expected == HttpStatusCode.UnprocessableEntity)
        {
            var problem = await ReadJson(response);
            problem.GetProperty("detail").GetString()
                .Should().Be(AccreditationApplicationEndpoints.MaxBesEvidenceFilesMessage);
            problem.GetProperty("code").GetString()
                .Should().Be(AccreditationApplicationEndpoints.MaxSamplingPlanFilesErrorCode);
            uploads.Should().HaveCount(clean + infected);
        }
        else
        {
            uploads.Should().HaveCount(clean + infected + 1);
        }
    }

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
        stored.Sites.Single().Status.Should().Be(ChangeItemStatus.InProgress);
    }

    [Fact]
    public async Task Submit_MissingDeclarationDetails_Returns400()
    {
        await AddSite(901910);

        var response = await _client.PostAsJsonAsync(SubmitUrl, new { fullName = "", jobTitle = "" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _factory.FakeChangeRequests.All.Single().Status.Should().Be(ChangeRequestStatus.InProgress);
    }

    // RA-739: Case Management requires the payment reference the operator was told to quote, so a submit
    // without one is refused here, before anything is saved, rather than failing at Case Management.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Submit_WithoutAPaymentReference_Returns400AndStaysInProgress(string? paymentReference)
    {
        await AddSite(901910);

        var response = await _client.PostAsJsonAsync(
            SubmitUrl,
            new { fullName = "Jane Smith", jobTitle = "Director", paymentReference },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJson(response)).GetProperty("errors").TryGetProperty("PaymentReference", out _).Should().BeTrue();
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.InProgress);
        stored.PaymentReference.Should().BeNull();
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
        // Flag off: no references, which the operator sees as "Not applicable".
        stored.SubmissionReference.Should().BeNull();
        stored.Sites.Single().CaseManagementReference.Should().BeNull();
        stored.Sites.Single().CaseManagementWorkItemId.Should().BeNull();
        SiteIn(submitted, 901910).GetProperty("caseManagementReference").ValueKind.Should().Be(JsonValueKind.Null);
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
    public async Task Submit_SetsEveryItemToSubmitted()
    {
        await AddSite(901910);
        await AddSite(901912);
        await Patch(901912, new { operationCodes = R3, conditionsOfExport = false });

        var response = await _client.PostAsJsonAsync(SubmitUrl, Declaration, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(response)).GetProperty("sites").EnumerateArray()
            .Select(s => s.GetProperty("status").GetString())
            .Should()
            .Equal("Submitted", "Submitted");
        _factory.FakeChangeRequests.All.Single().Sites.Select(s => s.Status)
            .Should()
            .Equal(ChangeItemStatus.Submitted, ChangeItemStatus.Submitted);
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

    // ---------------------------------------------------------------- RA-697 items

    private static readonly DateTime GrantedOn = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WithdrawnOn = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ChangeRequestSite Item(int siteId, string orsId, ChangeItemStatus status) =>
        new() { SiteId = siteId, OrsId = orsId, SiteName = $"Site {orsId}", Status = status };

    // Statuses beyond Submitted are only ever seeded until Case Management decides items.
    private AccreditationChangeRequest SeedSubmitted(
        DateTime createdAt,
        List<ChangeRequestSite> sites,
        TonnageBandUpgradeItem? tonnage = null
    )
    {
        var request = new AccreditationChangeRequest
        {
            OrganisationId = Org,
            ApplicationId = AppId,
            MaterialType = MaterialType.Plastic,
            Year = Year,
            ChangeType = tonnage is null
                ? AccreditationChangeRequest.AddOverseasSiteChangeType
                : AccreditationChangeRequest.UpgradeTonnageBandChangeType,
            Status = ChangeRequestStatus.Submitted,
            Sites = sites,
            TonnageBandUpgrade = tonnage,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            SubmittedAt = createdAt,
            SubmittedBy = new SubmittedByModel { FullName = "Jane Smith", JobTitle = "Director" },
            SubmissionReference = $"SUB-{createdAt:MMdd}",
        };
        _factory.FakeChangeRequests.Seed(request);
        return request;
    }

    private static JsonElement RequestIn(JsonElement view, string requestId) =>
        view.GetProperty("requests").EnumerateArray()
            .Single(r => r.GetProperty("requestId").GetString() == requestId);

    [Fact]
    public async Task Get_ReturnsEachRequestsItemsWithTheirStatusAndValidity_AndTheTonnageItem()
    {
        var granted = Item(901910, "005", ChangeItemStatus.Approved);
        granted.ValidFrom = GrantedOn;
        var withdrawn = Item(901911, "006", ChangeItemStatus.Withdrawn);
        withdrawn.ValidFrom = GrantedOn;
        withdrawn.WithdrawnAt = WithdrawnOn;
        granted.CaseManagementReference = "IYC-ORS-2026-00001";
        withdrawn.CaseManagementReference = "IYC-ORS-2026-00002";
        var sites = SeedSubmitted(GrantedOn, [granted, withdrawn]);
        var tonnage = SeedSubmitted(
            WithdrawnOn,
            [],
            new TonnageBandUpgradeItem
            {
                Status = ChangeItemStatus.Queried,
                RequestedBand = PlannedTonnageBand.UpTo10000,
                CaseManagementReference = "IYC-TBU-2026-00001",
            }
        );

        var view = await Get();

        var siteRequest = RequestIn(view, sites.RequestId);
        siteRequest.TryGetProperty("caseManagementReference", out _).Should().BeFalse();
        siteRequest.TryGetProperty("submissionReference", out _).Should().BeFalse();
        siteRequest.GetProperty("submittedBy").GetProperty("fullName").GetString().Should().Be("Jane Smith");
        siteRequest.GetProperty("submittedAt").GetDateTime().Should().Be(GrantedOn);
        siteRequest.GetProperty("tonnageBandUpgrade").ValueKind.Should().Be(JsonValueKind.Null);
        var grantedView = SiteIn(siteRequest, 901910);
        grantedView.GetProperty("itemId").GetGuid().Should().Be(granted.ItemId);
        grantedView.GetProperty("changeType").GetString().Should().Be("add-overseas-site");
        grantedView.GetProperty("status").GetString().Should().Be("Approved");
        grantedView.GetProperty("validFrom").GetDateTime().Should().Be(GrantedOn);
        grantedView.GetProperty("withdrawnAt").ValueKind.Should().Be(JsonValueKind.Null);
        grantedView.GetProperty("caseManagementReference").GetString().Should().Be("IYC-ORS-2026-00001");
        var withdrawnView = SiteIn(siteRequest, 901911);
        withdrawnView.GetProperty("status").GetString().Should().Be("Withdrawn");
        withdrawnView.GetProperty("withdrawnAt").GetDateTime().Should().Be(WithdrawnOn);
        withdrawnView.GetProperty("caseManagementReference").GetString().Should().Be("IYC-ORS-2026-00002");

        var tonnageRequest = RequestIn(view, tonnage.RequestId);
        tonnageRequest.GetProperty("changeType").GetString().Should().Be("upgrade-tonnage-band");
        tonnageRequest.GetProperty("sites").GetArrayLength().Should().Be(0);
        var item = tonnageRequest.GetProperty("tonnageBandUpgrade");
        item.GetProperty("itemId").GetGuid().Should().Be(tonnage.TonnageBandUpgrade!.ItemId);
        item.GetProperty("changeType").GetString().Should().Be("upgrade-tonnage-band");
        item.GetProperty("status").GetString().Should().Be("Queried");
        item.GetProperty("requestedBand").GetString().Should().Be("UpTo10000");
        item.GetProperty("validFrom").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("withdrawnAt").ValueKind.Should().Be(JsonValueKind.Null);
        item.GetProperty("caseManagementReference").GetString().Should().Be("IYC-TBU-2026-00001");
    }

    // A granted, refused or withdrawn item is history: the site is not pending and can be added again.
    [Fact]
    public async Task SiteWhoseItemsAreAllFinal_IsNotPendingAndCanBeAddedAgain()
    {
        SeedSubmitted(GrantedOn, [Item(901910, "005", ChangeItemStatus.Approved)]);
        SeedSubmitted(WithdrawnOn, [Item(901910, "005", ChangeItemStatus.Rejected), Item(901912, "007", ChangeItemStatus.Withdrawn)]);

        var view = await Get();

        foreach (var siteId in new[] { 901910, 901912 })
        {
            var site = SiteIn(view, siteId);
            site.GetProperty("pendingRequestId").ValueKind.Should().Be(JsonValueKind.Null);
            site.GetProperty("pendingRequestStatus").ValueKind.Should().Be(JsonValueKind.Null);
        }
        view.GetProperty("pendingOrsIds").GetArrayLength().Should().Be(0);
        (await AddSite(901910)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // Pending comes from the request whose item for the site is still active, even when a newer
    // request holds a final item for it; the value is that request's lifecycle status.
    [Fact]
    public async Task SiteWithAnActiveItemInASubmittedRequest_IsPendingAndCannotBeAdded()
    {
        var active = SeedSubmitted(GrantedOn, [Item(901910, "005", ChangeItemStatus.Queried)]);
        SeedSubmitted(WithdrawnOn, [Item(901910, "005", ChangeItemStatus.Rejected)]);

        var view = await Get();

        var site = SiteIn(view, 901910);
        site.GetProperty("pendingRequestId").GetString().Should().Be(active.RequestId);
        site.GetProperty("pendingRequestStatus").GetString().Should().Be("Submitted");
        Strings(view.GetProperty("pendingOrsIds")).Should().Equal("005");
        (await AddSite(901910)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ---------------------------------------------------------------- RA-697 registration-only requests

    private static readonly string RegistrationId = DevExporterOrg.InYearChange.RegistrationId.ToString();

    private AccreditationChangeRequest SeedRegistrationOnly(
        DateTime createdAt,
        List<ChangeRequestSite> sites,
        ChangeRequestStatus status = ChangeRequestStatus.Submitted,
        string? registrationId = null,
        MaterialType material = MaterialType.Plastic,
        int year = Year,
        string? organisationId = null
    )
    {
        var request = new AccreditationChangeRequest
        {
            OrganisationId = organisationId ?? Org,
            ApplicationId = null,
            RegistrationId = registrationId ?? RegistrationId,
            MaterialType = material,
            Year = year,
            ChangeType = AccreditationChangeRequest.UpdateOverseasSiteChangeType,
            Status = status,
            Sites = sites,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            SubmittedAt = status == ChangeRequestStatus.Submitted ? createdAt : null,
        };
        _factory.FakeChangeRequests.Seed(request);
        return request;
    }

    private static ChangeRequestSite SubmittedIndiaItem() => Item(901911, "006", ChangeItemStatus.Submitted);

    private static List<string> RequestIds(JsonElement view) =>
        view.GetProperty("requests").EnumerateArray().Select(r => r.GetProperty("requestId").GetString()!).ToList();

    [Fact]
    public async Task Get_MergesThisRegistrationsCurrentYearRegistrationOnlyRequests_NewestFirst()
    {
        var oldest = SeedSubmitted(GrantedOn, [Item(901912, "007", ChangeItemStatus.Approved)]);
        var registrationOnly = SeedRegistrationOnly(GrantedOn.AddDays(10), [SubmittedIndiaItem()]);
        var newest = SeedSubmitted(WithdrawnOn, [Item(901912, "007", ChangeItemStatus.Rejected)]);
        // Not this accreditation's registration, material, year or organisation.
        SeedRegistrationOnly(GrantedOn, [SubmittedIndiaItem()], registrationId: "aaa000000000000000099999");
        SeedRegistrationOnly(GrantedOn, [SubmittedIndiaItem()], material: MaterialType.Glass);
        SeedRegistrationOnly(GrantedOn, [SubmittedIndiaItem()], year: Year - 1);
        SeedRegistrationOnly(GrantedOn, [SubmittedIndiaItem()], organisationId: "99999");

        var view = await Get();

        RequestIds(view).Should().Equal(newest.RequestId, registrationOnly.RequestId, oldest.RequestId);
        var merged = RequestIn(view, registrationOnly.RequestId);
        merged.GetProperty("changeType").GetString().Should().Be("update-overseas-site");
        SiteIn(merged, 901911).GetProperty("status").GetString().Should().Be("Submitted");
    }

    [Fact]
    public async Task Get_RegistrationOnlyRequestCountsTowardsPendingStatus()
    {
        var registrationOnly = SeedRegistrationOnly(GrantedOn, [Item(901911, "006", ChangeItemStatus.Queried)]);

        var view = await Get();

        var site = SiteIn(view, 901911);
        site.GetProperty("pendingRequestId").GetString().Should().Be(registrationOnly.RequestId);
        site.GetProperty("pendingRequestStatus").GetString().Should().Be("Submitted");
        Strings(view.GetProperty("pendingOrsIds")).Should().Equal("006");
    }

    // A registration-only item is keyed by its ORS id's integer value (6 for ORS 006), not the
    // accreditation's site key (901911), so it is matched to the site by ORS id.
    private static ChangeRequestSite RegistrationKeyedIndiaItem(ChangeItemStatus status) => Item(6, "006", status);

    [Fact]
    public async Task Get_RegistrationOnlyItemKeyedByOrsNumber_CountsTowardsTheSitesPendingStatus()
    {
        var registrationOnly = SeedRegistrationOnly(GrantedOn, [RegistrationKeyedIndiaItem(ChangeItemStatus.Queried)]);

        var view = await Get();

        var site = SiteIn(view, 901911);
        site.GetProperty("pendingRequestId").GetString().Should().Be(registrationOnly.RequestId);
        site.GetProperty("pendingRequestStatus").GetString().Should().Be("Submitted");
    }

    [Fact]
    public async Task Get_RegistrationOnlyItemKeyedByOrsNumber_TakesItsEffectiveAnswersFromTheMatchingSite()
    {
        var registrationOnly = SeedRegistrationOnly(GrantedOn, [RegistrationKeyedIndiaItem(ChangeItemStatus.Submitted)]);

        var view = await Get();

        var country = SiteIn(view, 901911).GetProperty("site").GetProperty("country").GetString();
        country.Should().NotBeNullOrWhiteSpace();
        var item = SiteIn(RequestIn(view, registrationOnly.RequestId), 6);
        item.GetProperty("effective").GetProperty("country").GetString().Should().Be(country);
    }

    // Only one change per ORS can be active at a time, whichever kind of request holds it.
    [Fact]
    public async Task AddSite_RegistrationOnlyRequestHoldsAnActiveItemForTheOrs_Conflicts()
    {
        SeedRegistrationOnly(GrantedOn, [RegistrationKeyedIndiaItem(ChangeItemStatus.Submitted)]);

        (await AddSite(901911)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddSite_RegistrationOnlyDraftHoldsTheOrs_Conflicts()
    {
        SeedRegistrationOnly(
            GrantedOn,
            [RegistrationKeyedIndiaItem(ChangeItemStatus.InProgress)],
            ChangeRequestStatus.InProgress
        );

        (await AddSite(901911)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AddSite_RegistrationOnlyRequestsItemForTheOrsIsFinal_CanBeAdded()
    {
        SeedRegistrationOnly(GrantedOn, [RegistrationKeyedIndiaItem(ChangeItemStatus.Rejected)]);

        (await AddSite(901911)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // RA-492's journey and payment page act only on the accreditation's own draft.
    [Fact]
    public async Task Get_RegistrationOnlyDraft_NeverBecomesTheActiveRequest()
    {
        var registrationDraft = SeedRegistrationOnly(
            WithdrawnOn,
            [Item(901911, "006", ChangeItemStatus.InProgress)],
            ChangeRequestStatus.InProgress
        );

        var before = await Get();
        before.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Null);
        RequestIds(before).Should().Contain(registrationDraft.RequestId);

        var added = await AddSite(901910);
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var draftId = (await ReadJson(added)).GetProperty("requestId").GetString();
        draftId.Should().NotBe(registrationDraft.RequestId);
        var after = await Get();
        after.GetProperty("activeRequest").GetProperty("requestId").GetString().Should().Be(draftId);
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

    private static readonly string[] R3 = ["R3"];

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
            new { fullName = "Jane Smith", jobTitle = "Director", paymentReference = "PAY-1" },
            Ct
        );
    }

    // Case Management's answer for AddAndSubmit's one site, 901910.
    private static ChangeRequestSubmissionResult Sent((string Reference, Guid? WorkItemId) site) =>
        new("SUB-2026-0001", [new SiteWorkItemResult(901910, site.Reference, site.WorkItemId)]);

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
            .Returns(Sent(("IYC-ORS-2026-00001", workItemId)));

        var response = await AddAndSubmit();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.SubmissionReference.Should().Be("SUB-2026-0001");
        stored.Sites.Single().CaseManagementReference.Should().Be("IYC-ORS-2026-00001");
        stored.Sites.Single().CaseManagementWorkItemId.Should().Be(workItemId);
        await _factory.MockCaseWorkingAdapter.Received(1).SubmitChangeRequestAsync(
            Arg.Is<AccreditationChangeRequest>(r =>
                r.Sites.Single().OrsId == "005" && r.ChargePence == 32800 && r.SubmittedBy!.FullName == "Jane Smith"
            ),
            Arg.Is<AccreditationApplicationModel>(a => a.AccreditationReference == AccreditationReference),
            Arg.Any<CancellationToken>()
        );
    }

    private void CaseManagementReturns(ChangeRequestSubmissionResult result) =>
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
                return Sent(("IYC-ORS-2026-00001", null));
            });

        (await AddAndSubmit()).StatusCode.Should().Be(HttpStatusCode.OK);

        storedWhenCalled.Should().Be(ChangeRequestStatus.Submitted);
    }

    [Fact]
    public async Task Submit_FlagOn_LostRace_Returns409AndNeverCallsCaseManagement()
    {
        CaseManagementReturns(Sent(("IYC-ORS-2026-00001", null)));
        await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901910 }, Ct);
        _factory.FakeChangeRequests.LoseUpdateWhen = r => r.Status == ChangeRequestStatus.Submitted;

        var response = await _client.PostAsJsonAsync(
            $"{Base}/requests/current/submit",
            new { fullName = "Jane Smith", jobTitle = "Director", paymentReference = "PAY-1" },
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
        stored.SubmissionReference.Should().BeNull();
        stored.Sites.Single().CaseManagementReference.Should().BeNull();
        stored.Sites.Single().CaseManagementWorkItemId.Should().BeNull();
        stored.Sites.Single().ChargePence.Should().BeNull();
        stored.Sites.Single().Status.Should().Be(ChangeItemStatus.InProgress);
    }

    // Another draft was started while the request was Submitted: the one-draft rule refuses the
    // reopen, so the stored request and its items stay Submitted.
    [Fact]
    public async Task Submit_FlagOn_CaseManagementFailsAndTheReopenIsRefused_LeavesTheItemsSubmitted()
    {
        _factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<ChangeRequestSubmissionResult>(_ =>
            {
                _factory.FakeChangeRequests.Seed(
                    new AccreditationChangeRequest
                    {
                        OrganisationId = Org,
                        ApplicationId = AppId,
                        MaterialType = MaterialType.Plastic,
                        Year = Year,
                        Sites = [new ChangeRequestSite { SiteId = 901912, OrsId = "007", SiteName = "Other" }],
                    }
                );
                throw new HttpRequestException("unavailable");
            });

        var response = await AddAndSubmit();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var stored = _factory.FakeChangeRequests.All.Single(r => r.Sites.Exists(s => s.SiteId == 901910));
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.Sites.Single().Status.Should().Be(ChangeItemStatus.Submitted);
    }

    // Reopened, the request is the draft again: its site is not pending a regulator decision and a
    // retry submits it. Case Management may already hold the first attempt and replays a resubmit with
    // the sites it received, so the sites sent can't be added to or removed.
    [Theory]
    [InlineData("unreachable")]
    [InlineData("server error")]
    [InlineData("timeout")]
    public async Task Submit_FlagOn_AfterACaseManagementFailure_TheSitesAreFixedButTheDraftCanBeResubmitted(
        string failure
    )
    {
        CaseManagementThrows(
            failure switch
            {
                "timeout" => new CaseWorkingApiTimeoutException("timed out", new TaskCanceledException()),
                "server error" => new HttpRequestException("failed", null, HttpStatusCode.InternalServerError),
                _ => new HttpRequestException("unavailable"),
            }
        );
        (await AddAndSubmit()).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var view = await (await _client.GetAsync(Base, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        view.GetProperty("activeRequest").ValueKind.Should().Be(JsonValueKind.Object);
        view.GetProperty("sites").EnumerateArray()
            .Single(s => s.GetProperty("siteId").GetInt32() == 901910)
            .GetProperty("pendingRequestStatus").GetString().Should().Be("InProgress");
        _factory.FakeChangeRequests.All.Single().SentToCaseManagement.Should().BeTrue();
        (await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901912 }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.DeleteAsync($"{Base}/requests/current/sites/901910", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901910 }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        CaseManagementReturns(Sent(("IYC-ORS-2026-00001", null)));
        (await _client.PostAsJsonAsync($"{Base}/requests/current/submit", new { fullName = "Jane Smith", jobTitle = "Director", paymentReference = "PAY-1" }, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.Sites.Should().ContainSingle().Which.SiteId.Should().Be(901910);
        stored.SubmissionReference.Should().Be("SUB-2026-0001");
        stored.Sites.Single().CaseManagementReference.Should().Be("IYC-ORS-2026-00001");
    }

    // Not configured, nothing was sent; a 4xx, Case Management saved nothing. Either way the reopened
    // draft's sites can still change.
    [Theory]
    [InlineData("not-configured")]
    [InlineData("rejected")]
    public async Task Submit_FlagOn_CaseManagementHoldsNothing_TheSitesCanStillChange(string failure)
    {
        CaseManagementThrows(
            failure == "rejected"
                ? new HttpRequestException("invalid", null, HttpStatusCode.BadRequest)
                : new CaseWorkingNotConfiguredException()
        );
        (await AddAndSubmit()).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        _factory.FakeChangeRequests.All.Single().SentToCaseManagement.Should().BeFalse();
        (await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901912 }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.DeleteAsync($"{Base}/requests/current/sites/901910", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
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
        CaseManagementReturns(Sent(("IYC-ORS-2026-00001", null)));
        _factory.FakeChangeRequests.LoseUpdateWhen = r => r.SubmissionReference is not null;

        var response = await AddAndSubmit();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.SubmissionReference.Should().BeNull();
        stored.Sites.Single().CaseManagementReference.Should().BeNull();
    }
}

// RA-739: the hand-off flag on, through the real HTTP adapter, against a fake ManagementBe that
// answers POST /work-items as ManagementBe answers an add-overseas-site submission.
public sealed class AccreditationChangeCaseManagementHttpTests : IDisposable
{
    private const int Year = 2026;
    private static readonly string Org = DevExporterOrg.InYearChange.OrganisationId;
    private static readonly string AppId = DevExporterAccreditationSeedService.ApplicationIdFor(DevExporterOrg.InYearChange, Year).ToString();
    private static readonly string Base = $"/api/v1/accreditation-changes/{Org}/{AppId}";
    private static readonly string[] R3 = ["R3"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeManagementBe _managementBe = new();
    private readonly AccreditationChangeTestFactory _factory;
    private readonly HttpClient _client;

    // The seeded accreditation, held so a test can fill in an ORS before it is submitted.
    private readonly AccreditationApplicationModel _accreditation =
        DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year);

    public AccreditationChangeCaseManagementHttpTests()
    {
        _factory = new(submitToCaseManagement: true, caseManagement: _managementBe);
        _factory.FakePersistence.Seed(_accreditation);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _managementBe.Dispose();
    }

    // Three complete sites: France as it is, India and Turkey with their missing answers supplied.
    private async Task<HttpResponseMessage> SubmitThreeSites()
    {
        foreach (var siteId in new[] { 901910, 901911, 901912 })
            (await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        await _client.PatchAsJsonAsync(
            $"{Base}/requests/current/sites/901911",
            new { coordinates = "28.6139, 77.2090", operationCodes = R3, conditionsOfExport = true },
            Ct
        );
        await _client.PatchAsJsonAsync(
            $"{Base}/requests/current/sites/901912",
            new { operationCodes = R3, conditionsOfExport = false },
            Ct
        );
        return await _client.PostAsJsonAsync(
            $"{Base}/requests/current/submit",
            new { fullName = "Jane Smith", jobTitle = "Director", paymentReference = "PAY-1" },
            Ct
        );
    }

    // ManagementBe's 201: the sites in reverse order, each echoing the siteId number it was sent.
    private static (HttpStatusCode, string) Created(JsonNode request, Func<JsonNode, object?>? reference = null)
    {
        var sites = request["payload"]!["sites"]!.AsArray();
        var workItems = sites
            .Select((site, index) => new
            {
                siteId = site!["siteId"]!.GetValue<int>(),
                id = Guid.NewGuid(),
                applicationReference = reference is null ? $"IYC-ORS-2026-{index + 1:D5}" : reference(site),
            })
            .Reverse();
        return (HttpStatusCode.Created, JsonSerializer.Serialize(new { submissionReference = "SUB-2026-0001", workItems }));
    }

    [Fact]
    public async Task Submit_ThreeSites_StoresOneSubmissionReferenceAndEachSitesOwnReferenceAndWorkItem()
    {
        _managementBe.Respond = request => Created(request);

        var response = await SubmitThreeSites();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _managementBe.Requests.Should().ContainSingle();
        var sent = _managementBe.Requests.Single();
        sent["typeId"]!.GetValue<string>().Should().Be("add-overseas-site");
        sent["payload"]!["sites"]!.AsArray().Select(s => s!["siteId"]!.GetValue<int>())
            .Should().Equal(901910, 901911, 901912);

        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.SubmissionReference.Should().Be("SUB-2026-0001");
        stored.Sites.Select(s => (s.SiteId, s.CaseManagementReference))
            .Should().Equal((901910, "IYC-ORS-2026-00001"), (901911, "IYC-ORS-2026-00002"), (901912, "IYC-ORS-2026-00003"));
        stored.Sites.Select(s => s.CaseManagementWorkItemId).Should().OnlyHaveUniqueItems().And.NotContainNulls();

        // The operator's view shows each site's own reference, and not the submission reference.
        var view = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        view.GetProperty("sites").EnumerateArray()
            .Select(s => s.GetProperty("caseManagementReference").GetString())
            .Should().Equal("IYC-ORS-2026-00001", "IYC-ORS-2026-00002", "IYC-ORS-2026-00003");
        view.GetRawText().Should().NotContain("SUB-2026-0001");
    }

    private static JsonNode SentSite(JsonNode request, int siteId) =>
        request["payload"]!["sites"]!.AsArray().Single(s => s!["siteId"]!.GetValue<int>() == siteId)!;

    private async Task AttachCleanBesEvidence(int siteId)
    {
        var fileUploadId = Guid.NewGuid().ToString();
        await _factory.FakePendingUploadService.CreateAsync(fileUploadId, "http://cdp/status", ct: Ct);
        await _factory.FakePendingUploadService.CompleteAsync(
            fileUploadId,
            new CdpCallbackFile
            {
                FileId = "file-1",
                Filename = "bes.pdf",
                FileStatus = "complete",
                ContentType = "application/pdf",
                S3Key = "bes-evidence/file-1",
                S3Bucket = "bes-bucket",
            },
            Ct
        );
        (await _client.PostAsJsonAsync(
                $"{Base}/requests/current/sites/{siteId}/bes-evidence/files",
                new { fileUploadId, besEvidenceValidFromDate = "2026-01-01", besEvidenceExpiryDate = "2027-12-31" },
                Ct
            ))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);
    }

    // RA-739: each site goes in re-accreditation's site shape, with every interim site (withdrawn ones
    // carrying removedAt), and its own charge alongside the submission's total.
    [Fact]
    public async Task Submit_SendsEachSiteInReaccreditationsSiteShapeWithItsOwnCharge()
    {
        var lyon = _accreditation.OverseasSites!.Sites.Single(s => s.SiteId == 901910);
        lyon.AddressLine2 = "Zone Industrielle";
        lyon.Code2 = "B3020";
        lyon.Code3 = "GH013";
        lyon.InterimSites =
        [
            new InterimSiteModel
            {
                SiteId = 1,
                SiteNumber = "001",
                Country = "France",
                SiteName = "Marseille Storage",
                AddressLine1 = "1 Quai",
                TownOrCity = "Marseille",
                ContactName = "Alex Morgan",
                ContactEmail = "alex@example.com",
                ContactPhone = "0123456789",
                OperationCodes = ["R13"],
                CreatedAt = new DateTime(2025, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            new InterimSiteModel
            {
                SiteId = 2,
                SiteNumber = "002",
                Country = "France",
                SiteName = "Old Depot",
                AddressLine1 = "2 Quai",
                TownOrCity = "Lyon",
                ContactName = "Sam Lee",
                ContactEmail = "sam@example.com",
                ContactPhone = "0987654321",
                OperationCodes = ["R12"],
                RemovedAt = new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Utc),
            },
        ];
        _managementBe.Respond = request => Created(request);

        (await SubmitThreeSites()).StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = _managementBe.Requests.Single();
        sent["payload"]!["chargePence"]!.GetValue<int>().Should().Be(98400);
        var expected = JsonNode.Parse(
            """
            {
              "siteId": 901910,
              "orsId": "005",
              "siteName": "Lyon Plastiques SA",
              "siteAddress": "901910 Industrial Estate, France",
              "addressLine1": "901910 Industrial Estate",
              "addressLine2": "Zone Industrielle",
              "townOrCity": "Port",
              "country": "France",
              "coordinates": "45.7640, 4.8357",
              "contactName": "Site Manager",
              "contactEmail": "site901910@example.com",
              "contactPhone": "+44 1234 567890",
              "operationCodes": ["R3"],
              "code1": "B3011",
              "code2": "B3020",
              "code3": "GH013",
              "repatriatedLoads": "Rejected loads are returned to the UK exporter.",
              "conditionsOfExport": true,
              "isEu": true,
              "isOecd": true,
              "interimSites": [
                {
                  "siteId": 1,
                  "siteNumber": "001",
                  "isNewSite": false,
                  "country": "France",
                  "siteName": "Marseille Storage",
                  "addressLine1": "1 Quai",
                  "townOrCity": "Marseille",
                  "contactName": "Alex Morgan",
                  "contactEmail": "alex@example.com",
                  "contactPhone": "0123456789",
                  "operationCodes": ["R13"],
                  "createdAt": "2025-09-01T00:00:00Z"
                },
                {
                  "siteId": 2,
                  "siteNumber": "002",
                  "isNewSite": false,
                  "country": "France",
                  "siteName": "Old Depot",
                  "addressLine1": "2 Quai",
                  "townOrCity": "Lyon",
                  "contactName": "Sam Lee",
                  "contactEmail": "sam@example.com",
                  "contactPhone": "0987654321",
                  "operationCodes": ["R12"],
                  "removedAt": "2026-02-01T09:00:00Z"
                }
              ],
              "besEvidence": { "files": [] },
              "suppliedFields": [],
              "chargePence": 32800
            }
            """
        )!;
        var lyonSent = SentSite(sent, 901910);
        JsonNode.DeepEquals(lyonSent, expected)
            .Should()
            .BeTrue($"expected {expected.ToJsonString()} but was {lyonSent.ToJsonString()}");
        SentSite(sent, 901912)["chargePence"]!.GetValue<int>().Should().Be(32800);
    }

    // The operator's answer stands in for each value the ORS lacks, and the BES evidence is the request's own.
    [Fact]
    public async Task Submit_SendsTheEffectiveValuesAndTheRequestsOwnBesEvidence()
    {
        _managementBe.Respond = request => Created(request);
        (await _client.PostAsJsonAsync($"{Base}/requests/current/sites", new { siteId = 901911 }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await _client.PatchAsJsonAsync(
                $"{Base}/requests/current/sites/901911",
                new { coordinates = "28.6139, 77.2090", operationCodes = R3, conditionsOfExport = false },
                Ct
            ))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        await AttachCleanBesEvidence(901911);

        (await _client.PostAsJsonAsync(
                $"{Base}/requests/current/submit",
                new { fullName = "Jane Smith", jobTitle = "Director", paymentReference = "PAY-1" },
                Ct
            ))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        var sent = _managementBe.Requests.Single();
        sent["payload"]!["chargePence"]!.GetValue<int>().Should().Be(32800);
        var site = SentSite(sent, 901911);
        site["country"]!.GetValue<string>().Should().Be("India");
        site["coordinates"]!.GetValue<string>().Should().Be("28.6139, 77.2090");
        site["operationCodes"]!.AsArray().Select(c => c!.GetValue<string>()).Should().Equal("R3");
        site["conditionsOfExport"]!.GetValue<bool>().Should().BeFalse();
        site["isEu"]!.GetValue<bool>().Should().BeFalse();
        site["isOecd"]!.GetValue<bool>().Should().BeFalse();
        site["suppliedFields"]!.AsArray().Select(f => f!.GetValue<string>())
            .Should().Equal("coordinates", "operationCodes", "conditionsOfExport");
        site["chargePence"]!.GetValue<int>().Should().Be(32800);
        var file = site["besEvidence"]!["files"]!.AsArray().Should().ContainSingle().Subject!;
        file["fileId"]!.GetValue<string>().Should().Be("file-1");
        file["filename"]!.GetValue<string>().Should().Be("bes.pdf");
        file["contentType"]!.GetValue<string>().Should().Be("application/pdf");
        file["scanStatus"]!.GetValue<string>().Should().Be("Clean");
        file["besEvidenceValidFromDate"]!.GetValue<string>().Should().Be("2026-01-01");
        file["besEvidenceExpiryDate"]!.GetValue<string>().Should().Be("2027-12-31");
        file["s3Key"]!.GetValue<string>().Should().Be("bes-evidence/file-1");
        file["s3Bucket"]!.GetValue<string>().Should().Be("bes-bucket");
        file["uploadedAt"].Should().NotBeNull();
    }

    // Each site's own charge is stored with the request when it is submitted.
    [Fact]
    public async Task Submit_StoresEachSitesOwnChargeAndTheTotal()
    {
        _managementBe.Respond = request => Created(request);

        (await SubmitThreeSites()).StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = _factory.FakeChangeRequests.All.Single();
        stored.ChargePence.Should().Be(98400);
        stored.Sites.Select(s => s.ChargePence).Should().Equal(32800, 32800, 32800);
    }

    public static TheoryData<string> Failures() =>
        ["server error", "unparseable", "no submission reference", "a site without a reference", "a site missing", "timeout"];

    // Any Case Management failure reopens the request, with nothing of the submission kept, and is a 503.
    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Submit_CaseManagementFails_Returns503AndReopensTheRequest(string failure)
    {
        _managementBe.Respond = request =>
            failure switch
            {
                "server error" => (HttpStatusCode.InternalServerError, """{"title":"Error"}"""),
                "unparseable" => (HttpStatusCode.Created, "<html>"),
                "no submission reference" => (
                    HttpStatusCode.Created,
                    Created(request).Item2.Replace("\"submissionReference\":\"SUB-2026-0001\",", "", StringComparison.Ordinal)
                ),
                "a site without a reference" => Created(
                    request,
                    site => site["siteId"]!.GetValue<int>() == 901911 ? null : "IYC-ORS-2026-00009"
                ),
                "a site missing" => (
                    HttpStatusCode.Created,
                    """{"submissionReference":"SUB-2026-0001","workItems":[{"siteId":901910,"applicationReference":"IYC-ORS-2026-00001"}]}"""
                ),
                _ => throw new TaskCanceledException("timed out", new TimeoutException()),
            };

        var response = await SubmitThreeSites();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.InProgress);
        stored.SubmittedAt.Should().BeNull();
        stored.SubmittedBy.Should().BeNull();
        stored.PaymentReference.Should().BeNull();
        stored.ChargePence.Should().BeNull();
        stored.SubmissionReference.Should().BeNull();
        stored.Sites.Should().HaveCount(3);
        stored.Sites.Should().OnlyContain(s =>
            s.Status == ChangeItemStatus.InProgress
            && s.CaseManagementReference == null
            && s.CaseManagementWorkItemId == null
            && s.ChargePence == null
        );
    }

    private sealed class FakeManagementBe : HttpMessageHandler
    {
        public Func<JsonNode, (HttpStatusCode Status, string Body)> Respond { get; set; } =
            _ => (HttpStatusCode.NotFound, "{}");

        public List<JsonNode> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            request.RequestUri!.ToString().Should().Be($"{AccreditationChangeTestFactory.CaseManagementUrl}/work-items");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            Requests.Add(body);
            var (status, json) = Respond(body);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
