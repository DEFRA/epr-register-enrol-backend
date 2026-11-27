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
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

/// <summary>
/// RA-682: changing an accredited ORS's details (and its interim sites) as an in-year change, alongside RA-492's
/// adds in the same request. Org 50025: ORSs 902501 (with interim sites 902511 and 902512), 902502 and 902503 are
/// accredited; 902504 and 902505 are registered.
/// </summary>
public sealed class UpdateOverseasSiteEndpointTests : IDisposable
{
    private const int Year = 2026;
    private const string UpdateType = "update-overseas-site";
    private static readonly string Org = DevExporterOrg.InYearUpdate.OrganisationId;
    private static readonly string AppId =
        DevExporterAccreditationSeedService.ApplicationIdFor(DevExporterOrg.InYearUpdate, Year).ToString();
    private static readonly string Base = $"/api/v1/accreditation-changes/{Org}/{AppId}";
    private static readonly string Sites = $"{Base}/requests/current/sites";
    private static readonly string SubmitUrl = $"{Base}/requests/current/submit";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AccreditationChangeTestFactory _factory;
    private readonly HttpClient _client;

    // The seeded accreditation, which the fake persistence holds by reference, so a test can change an ORS in place.
    private readonly AccreditationApplicationModel _accreditation =
        DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, Year);

    public UpdateOverseasSiteEndpointTests()
    {
        _factory = new AccreditationChangeTestFactory(submitToCaseManagement: false);
        _factory.FakePersistence.Seed(_accreditation);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private Task<HttpResponseMessage> StartUpdate(int siteId) =>
        _client.PostAsJsonAsync(Sites, new { siteId, changeType = UpdateType }, Ct);

    private Task<HttpResponseMessage> Add(int siteId) => _client.PostAsJsonAsync(Sites, new { siteId }, Ct);

    private Task<HttpResponseMessage> Patch(int siteId, object body) =>
        _client.PatchAsJsonAsync($"{Sites}/{siteId}", body, Ct);

    private static JsonElement SiteIn(JsonElement request, int siteId) =>
        request.GetProperty("sites").EnumerateArray().Single(s => s.GetProperty("siteId").GetInt32() == siteId);

    private static object Interim(string siteName, string country = "Poland", string[]? codes = null) =>
        new
        {
            country,
            siteName,
            addressLine1 = "1 Quay Street",
            townOrCity = "Port",
            contactName = "Alex Morgan",
            contactEmail = "alex.morgan@example.com",
            contactPhone = "0123456789",
            operationCodes = codes ?? ["R3", "R13"],
        };

    private static object Declaration(string? paymentReference) =>
        new { fullName = "Jane Smith", jobTitle = "Director", paymentReference };

    [Fact]
    public async Task Starting_an_update_snapshots_the_accredited_ORS_details_as_previous_and_proposed()
    {
        var response = await StartUpdate(902501);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = SiteIn(await Json(response), 902501);
        site.GetProperty("changeType").GetString().Should().Be(UpdateType);
        site.GetProperty("previous").GetProperty("siteName").GetString().Should().Be("Gdansk Recykling Sp. z o.o.");
        site.GetProperty("proposed").GetProperty("contactName").GetString().Should().Be("Site Manager");
        // Nothing has changed yet, so it can't be submitted.
        site.GetProperty("missingFields").EnumerateArray().Select(f => f.GetString()).Should().Equal("changes");
    }

    [Fact]
    public async Task Only_an_accredited_ORS_can_be_updated_and_only_a_registered_one_added()
    {
        // RA-814: the operator is told these ORSs are approved, though the status is still Accredited.
        var update = await StartUpdate(902504);
        update.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await update.Content.ReadAsStringAsync(Ct)).Should().Contain("Only an approved overseas site's details can be changed.");
        var add = await Add(902501);
        add.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await add.Content.ReadAsStringAsync(Ct)).Should().Contain("This overseas site is already approved.");
        (await _client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = "remove-overseas-site" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Starting_the_same_ORS_again_keeps_one_item()
    {
        (await StartUpdate(902501)).StatusCode.Should().Be(HttpStatusCode.Created);
        var again = await StartUpdate(902501);

        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(again)).GetProperty("sites").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Patching_an_update_changes_its_proposed_details_and_keeps_its_name_for_listing()
    {
        await StartUpdate(902501);

        var response = await Patch(902501, new { siteName = "Gdansk Recycling", contactEmail = "new@example.com" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var site = SiteIn(await Json(response), 902501);
        site.GetProperty("proposed").GetProperty("siteName").GetString().Should().Be("Gdansk Recycling");
        site.GetProperty("proposed").GetProperty("contactEmail").GetString().Should().Be("new@example.com");
        site.GetProperty("previous").GetProperty("contactEmail").GetString().Should().Be("site902501@example.com");
        site.GetProperty("siteName").GetString().Should().Be("Gdansk Recykling Sp. z o.o.");
        site.GetProperty("isComplete").GetBoolean().Should().BeTrue();
    }

    // AC02: the form shows the locked fields as read-only inputs, so they arrive with the ORS's own values.
    [Fact]
    public async Task A_locked_field_sent_unchanged_is_accepted_and_one_changed_is_refused()
    {
        await StartUpdate(902501);

        (await Patch(902501, new { country = "Poland", coordinates = "54.3520, 18.6466", siteName = "Gdansk" }))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        var refused = await Patch(902501, new { country = "Germany" });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain("country");
    }

    [Fact]
    public async Task An_invalid_detail_is_refused()
    {
        await StartUpdate(902501);

        (await Patch(902501, new { contactEmail = "not-an-email" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Patch(902501, new { siteName = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_add_items_details_cannot_be_patched()
    {
        await Add(902504);

        (await Patch(902504, new { siteName = "Renamed" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // AC04: interim sites are changed, removed and added as part of the ORS's update.
    [Fact]
    public async Task Interim_sites_can_be_changed_removed_added_and_undone()
    {
        await StartUpdate(902501);
        var interim = $"{Sites}/902501/interim-sites";

        (await _client.PatchAsJsonAsync($"{interim}/902511", Interim("Gdynia Hub Renamed"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await _client.DeleteAsync($"{interim}/902512", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        var added = await _client.PostAsJsonAsync(interim, Interim("Krakow Depot"), Ct);

        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var changes = SiteIn(await Json(added), 902501).GetProperty("interimSiteChanges").EnumerateArray().ToList();
        changes.Select(c => c.GetProperty("action").GetString()).Should().Equal("change", "remove", "add");
        changes[0].GetProperty("previous").GetProperty("siteName").GetString().Should().Be("Gdynia Transfer Hub");
        changes[0].GetProperty("proposed").GetProperty("siteName").GetString().Should().Be("Gdynia Hub Renamed");
        changes[2].GetProperty("interimSiteId").ValueKind.Should().Be(JsonValueKind.Null);

        var additionId = changes[2].GetProperty("changeId").GetGuid();
        var undone = await _client.DeleteAsync($"{Sites}/902501/interim-site-changes/{additionId}", Ct);
        undone.StatusCode.Should().Be(HttpStatusCode.OK);
        SiteIn(await Json(undone), 902501).GetProperty("interimSiteChanges").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task An_interim_sites_country_and_recycling_operations_cannot_be_changed()
    {
        await StartUpdate(902501);
        var interim = $"{Sites}/902501/interim-sites/902511";

        (await _client.PatchAsJsonAsync(interim, Interim("Gdynia Transfer Hub", country: "Germany"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
        (await _client.PatchAsJsonAsync(interim, Interim("Gdynia Transfer Hub", codes: ["R4", "R12"]), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Changing_an_interim_site_back_to_what_it_was_drops_the_change()
    {
        await StartUpdate(902501);
        var interim = $"{Sites}/902501/interim-sites/902511";
        await _client.PatchAsJsonAsync(interim, Interim("Gdynia Hub Renamed"), Ct);

        var back = await _client.PatchAsJsonAsync(interim, Interim("Gdynia Transfer Hub"), Ct);

        SiteIn(await Json(back), 902501).GetProperty("interimSiteChanges").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Interim_sites_can_only_be_changed_on_an_update_item()
    {
        await Add(902504);

        (await _client.PostAsJsonAsync($"{Sites}/902504/interim-sites", Interim("Depot"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_cannot_be_submitted()
    {
        await StartUpdate(902501);

        var response = await _client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("sites[902501].changes");
    }

    // AC05: two updates and one add are charged for the add alone, so a payment reference is needed.
    [Fact]
    public async Task A_mixed_request_is_charged_for_its_adds_only()
    {
        await StartUpdate(902501);
        await Patch(902501, new { siteName = "Gdansk Recycling" });
        await StartUpdate(902502);
        await Patch(902502, new { townOrCity = "Porto" });
        var view = await Json(await Add(902504));

        view.GetProperty("charge").GetProperty("chargeableSites").GetInt32().Should().Be(1);
        (await _client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var submitted = await _client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);

        submitted.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.ChargePence.Should().Be(view.GetProperty("charge").GetProperty("chargePerSitePence").GetInt32());
        stored.Sites.Single(s => s.SiteId == 902504).ChargePence.Should().BeGreaterThan(0);
        stored.Sites.Where(s => s.IsUpdate).Select(s => s.ChargePence).Should().Equal(0, 0);
    }

    [Fact]
    public async Task An_update_only_request_is_free_and_needs_no_payment_reference()
    {
        await StartUpdate(902503);
        await _client.PostAsJsonAsync($"{Sites}/902503/interim-sites", Interim("Turin Depot", country: "Italy"), Ct);

        var response = await _client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.ChargePence.Should().Be(0);
        stored.PaymentReference.Should().BeNull();
    }

    // AC03: once submitted, the ORS is held until the regulator decides, whatever the change type.
    [Fact]
    public async Task A_submitted_update_holds_its_ORS_but_not_the_others()
    {
        await StartUpdate(902501);
        await Patch(902501, new { siteName = "Gdansk Recycling" });
        (await _client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await StartUpdate(902501)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StartUpdate(902502)).StatusCode.Should().Be(HttpStatusCode.Created);
        var view = await Json(await _client.GetAsync(Base, Ct));
        var held = view.GetProperty("sites").EnumerateArray().Single(s => s.GetProperty("siteId").GetInt32() == 902501);
        held.GetProperty("pendingRequestStatus").GetString().Should().Be("Submitted");
    }

    [Fact]
    public async Task A_mixed_request_sent_to_case_management_takes_every_sites_reference()
    {
        using var factory = new AccreditationChangeTestFactory(submitToCaseManagement: true);
        factory.FakePersistence.Seed(
            DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, Year)
        );
        factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new ChangeRequestSubmissionResult(
                    [
                        new SiteWorkItemResult(902504, "IYC-ORS-2026-00001", Guid.NewGuid()),
                        new SiteWorkItemResult(902501, "IYC-ORS-2026-00002", Guid.NewGuid()),
                    ],
                    ["SUB-2026-0001", "SUB-2026-0002"]
                )
            );
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = UpdateType }, Ct);
        await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Recycling" }, Ct);
        await client.PostAsJsonAsync(Sites, new { siteId = 902504 }, Ct);

        var response = await client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = factory.FakeChangeRequests.All.Single();
        stored.Sites.Single(s => s.SiteId == 902501).CaseManagementReference.Should().Be("IYC-ORS-2026-00002");
        stored.Sites.Single(s => s.SiteId == 902504).CaseManagementReference.Should().Be("IYC-ORS-2026-00001");
        stored.SubmissionReference.Should().Be("SUB-2026-0001");
        stored.SubmissionReferences.Should().Equal("SUB-2026-0001", "SUB-2026-0002");
        stored.ChangeType.Should().Be(AccreditationChangeRequest.AddOverseasSiteChangeType);
    }

    // Case Management held the add group and refused the update group before saving it (a 400): the add is fixed,
    // and the update can still be changed or taken out.
    [Fact]
    public async Task A_refused_update_group_can_be_removed_while_the_held_add_stays_fixed()
    {
        using var factory = new AccreditationChangeTestFactory(submitToCaseManagement: true);
        factory.FakePersistence.Seed(
            DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, Year)
        );
        factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(
                new ChangeRequestPartlySubmittedException(
                    [AccreditationChangeRequest.AddOverseasSiteChangeType],
                    UpdateType,
                    refusedMayBeHeld: false,
                    new HttpRequestException("refused", null, HttpStatusCode.BadRequest)
                )
            );
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = UpdateType }, Ct);
        await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Recycling" }, Ct);
        await client.PostAsJsonAsync(Sites, new { siteId = 902504 }, Ct);

        var response = await client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);

        response.IsSuccessStatusCode.Should().BeFalse();
        var stored = factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.InProgress);
        stored.SentToCaseManagement.Should().BeTrue();
        stored.Sites.Single(s => s.SiteId == 902504).CaseManagementMayHold.Should().BeTrue();
        stored.Sites.Single(s => s.SiteId == 902501).CaseManagementMayHold.Should().BeFalse();

        (await client.DeleteAsync($"{Sites}/902504", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Again" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"{Sites}/902501", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // The submission timed out: Case Management may hold it and would replay it, so the update's details are fixed.
    [Fact]
    public async Task An_update_that_may_be_held_cannot_be_changed()
    {
        using var factory = new AccreditationChangeTestFactory(submitToCaseManagement: true);
        factory.FakePersistence.Seed(
            DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, Year)
        );
        factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .ThrowsAsync(new CaseWorkingApiTimeoutException("timed out", new TimeoutException()));
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = UpdateType }, Ct);
        await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Recycling" }, Ct);
        await client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);

        var interim = $"{Sites}/902501/interim-sites";
        (await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Other" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync(interim, Interim("Depot"), Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"{interim}/902512", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"{Sites}/902501", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // AC02: an ORS with no stored coordinates shows a blank read-only input, which is posted back blank.
    [Fact]
    public async Task A_blank_locked_value_matches_a_missing_one()
    {
        ChangeRequestRules.FindSite(_accreditation, 902502)!.Coordinates = null;
        await StartUpdate(902502);

        var response = await Patch(902502, new { coordinates = "", country = " Portugal ", addressLine1 = "2 New Road" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SiteIn(await Json(response), 902502).GetProperty("proposed").GetProperty("addressLine1").GetString()
            .Should().Be("2 New Road");
    }

    [Theory]
    [InlineData("coordinates", "1.0, 2.0")]
    [InlineData("conditionsOfExport", false)]
    public async Task Other_locked_fields_cannot_be_changed(string field, object value)
    {
        await StartUpdate(902501);

        var refused = await Patch(902501, new Dictionary<string, object> { [field] = value });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(refused)).GetProperty("errors").TryGetProperty(field, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Changed_recycling_operations_are_refused()
    {
        await StartUpdate(902501);

        var refused = await Patch(902501, new { operationCodes = new[] { "R4" } });

        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(refused)).GetProperty("errors").TryGetProperty("operationCodes", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Optional_details_can_be_cleared()
    {
        await StartUpdate(902501);

        var response = await Patch(902501, new { addressLine2 = "", contactPhone = "" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var proposed = SiteIn(await Json(response), 902501).GetProperty("proposed");
        proposed.GetProperty("addressLine2").ValueKind.Should().Be(JsonValueKind.Null);
        proposed.GetProperty("contactPhone").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // AC07: the previous details are the ORS's when the request is submitted; details the operator didn't change
    // follow the ORS too.
    [Fact]
    public async Task Submitting_takes_previous_details_from_the_ORS_as_it_is_then()
    {
        await StartUpdate(902501);
        await Patch(902501, new { siteName = "Gdansk Recycling" });
        ChangeRequestRules.FindSite(_accreditation, 902501)!.ContactName = "New Manager";

        (await _client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var site = _factory.FakeChangeRequests.All.Single().Sites.Single();
        site.Previous!.ContactName.Should().Be("New Manager");
        site.Proposed!.ContactName.Should().Be("New Manager");
        site.Proposed.SiteName.Should().Be("Gdansk Recycling");
    }

    [Fact]
    public async Task An_update_only_request_is_stored_as_an_update()
    {
        await StartUpdate(902501);
        await Patch(902501, new { siteName = "Gdansk Recycling" });

        await _client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);

        _factory.FakeChangeRequests.All.Single().ChangeType.Should().Be(UpdateType);
    }

    // AC02: an update can upload new BES evidence but not amend what the ORS holds.
    [Fact]
    public async Task An_update_can_add_BES_evidence_and_take_it_out_again_but_not_the_ORS_own()
    {
        var ors = TakesBesEvidence(902501);
        var started = SiteIn(await Json(await StartUpdate(902501)), 902501);
        started.GetProperty("besEvidenceUploadAllowed").GetBoolean().Should().BeTrue();
        started.GetProperty("besEvidenceRequired").GetBoolean().Should().BeFalse();

        var uploaded = await UploadBes(902501, "new-1", "new.pdf");

        uploaded.StatusCode.Should().Be(HttpStatusCode.Created);
        var site = SiteIn(await Json(uploaded), 902501);
        site.GetProperty("besEvidence").GetProperty("besEvidenceUploads").EnumerateArray()
            .Select(f => f.GetProperty("fileId").GetString()).Should().Equal("new-1");
        // New evidence is a change on its own.
        site.GetProperty("isComplete").GetBoolean().Should().BeTrue();

        (await _client.DeleteAsync($"{Sites}/902501/bes-evidence/files/held-1", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.UnprocessableEntity);
        ors.BesEvidence!.BesEvidenceUploads.Select(f => f.FileId).Should().Equal("held-1");

        var removed = await _client.DeleteAsync($"{Sites}/902501/bes-evidence/files/new-1", Ct);
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        SiteIn(await Json(removed), 902501).GetProperty("besEvidence").GetProperty("besEvidenceUploads")
            .GetArrayLength().Should().Be(0);
        (await _client.DeleteAsync($"{Sites}/902501/bes-evidence/files/unknown", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_updates_new_BES_evidence_cannot_share_a_name_with_the_ORS_own()
    {
        TakesBesEvidence(902501);
        await StartUpdate(902501);

        (await UploadBes(902501, "new-1", "held.pdf")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        _factory.FakeChangeRequests.All.Single().Sites.Single().BesEvidence.Should().BeNull();
    }

    [Fact]
    public async Task An_update_to_an_ORS_that_takes_no_BES_evidence_takes_no_upload()
    {
        await StartUpdate(902501);

        var view = await Json(await _client.GetAsync(Base, Ct));
        SiteIn(view.GetProperty("activeRequest"), 902501)
            .GetProperty("besEvidenceUploadAllowed").GetBoolean().Should().BeFalse();
        (await UploadBes(902501, "new-1", "new.pdf")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_update_with_only_new_BES_evidence_is_submitted_with_it()
    {
        TakesBesEvidence(902501);
        await StartUpdate(902501);
        await UploadBes(902501, "new-1", "new.pdf");

        (await _client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Sites.Single().BesEvidence!.BesEvidenceUploads.Select(f => f.FileId).Should().Equal("new-1");
        stored.ChargePence.Should().Be(0);
    }

    // Makes the seeded ORS one that takes BES evidence (outside the EU and OECD, conditions of export not met), holding
    // one file of its own.
    private OverseasSiteModel TakesBesEvidence(int siteId)
    {
        var ors = ChangeRequestRules.FindSite(_accreditation, siteId)!;
        ors.IsEu = false;
        ors.IsOecd = false;
        ors.ConditionsOfExport = false;
        ors.BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads =
            [
                new BesEvidenceFileModel { FileId = "held-1", Filename = "held.pdf", S3Key = "bes-evidence/held-1", ScanStatus = "Clean" },
            ],
        };
        return ors;
    }

    private async Task<HttpResponseMessage> UploadBes(int siteId, string fileId, string filename)
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
        return await _client.PostAsJsonAsync($"{Sites}/{siteId}/bes-evidence/files", new { fileUploadId }, Ct);
    }

    [Fact]
    public async Task A_pending_interim_site_addition_can_be_edited()
    {
        await StartUpdate(902501);
        var added = await _client.PostAsJsonAsync($"{Sites}/902501/interim-sites", Interim("Krakow Depot"), Ct);
        var changeId = SiteIn(await Json(added), 902501).GetProperty("interimSiteChanges")[0].GetProperty("changeId").GetGuid();

        var edited = await _client.PatchAsJsonAsync(
            $"{Sites}/902501/interim-site-changes/{changeId}",
            Interim("Krakow Depot Two"),
            Ct
        );

        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        SiteIn(await Json(edited), 902501).GetProperty("interimSiteChanges")[0].GetProperty("proposed")
            .GetProperty("siteName").GetString().Should().Be("Krakow Depot Two");
    }

    [Fact]
    public async Task Unknown_interim_sites_and_changes_are_not_found()
    {
        await StartUpdate(902501);

        (await _client.PatchAsJsonAsync($"{Sites}/902501/interim-sites/999999", Interim("X"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"{Sites}/902501/interim-sites/999999", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.DeleteAsync($"{Sites}/902501/interim-site-changes/{Guid.NewGuid()}", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
        (await _client.PostAsJsonAsync($"{Sites}/902502/interim-sites", Interim("X"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    // A factory that sends to Case Management, with its own seeded accreditation (held by reference, so a test can
    // change an ORS in place).
    private static (AccreditationChangeTestFactory Factory, HttpClient Client, AccreditationApplicationModel Seeded)
        CaseManagementFactory()
    {
        var factory = new AccreditationChangeTestFactory(submitToCaseManagement: true);
        var seeded = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, Year);
        factory.FakePersistence.Seed(seeded);
        return (factory, factory.CreateClient(), seeded);
    }

    private static void SubmitFails(AccreditationChangeTestFactory factory, params Exception[] failures)
    {
        var queue = new Queue<Exception>(failures);
        factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns<Task<ChangeRequestSubmissionResult>>(_ => throw queue.Dequeue());
    }

    private static async Task StartMixedRequest(HttpClient client)
    {
        await client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = UpdateType }, Ct);
        await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Recycling" }, Ct);
        await client.PostAsJsonAsync(Sites, new { siteId = 902504 }, Ct);
    }

    private static ChangeRequestPartlySubmittedException UpdateGroupRefused() =>
        new(
            [AccreditationChangeRequest.AddOverseasSiteChangeType],
            UpdateType,
            refusedMayBeHeld: false,
            new HttpRequestException("refused", null, HttpStatusCode.BadRequest)
        );

    // The add group goes first, so when it times out the update group was never sent: only the adds are fixed, and
    // when a resubmit's update group is then refused, the update can still be taken out.
    [Fact]
    public async Task A_first_group_timeout_fixes_only_that_group()
    {
        var (factory, client, _) = CaseManagementFactory();
        using var disposeFactory = factory;
        using var disposeClient = client;
        SubmitFails(factory, new CaseWorkingApiTimeoutException("timed out", new TimeoutException()), UpdateGroupRefused());
        await StartMixedRequest(client);

        await client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);
        var afterTimeout = factory.FakeChangeRequests.All.Single();
        afterTimeout.Sites.Single(s => s.SiteId == 902504).CaseManagementMayHold.Should().BeTrue();
        afterTimeout.Sites.Single(s => s.SiteId == 902501).CaseManagementMayHold.Should().BeFalse();

        await client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);

        (await client.DeleteAsync($"{Sites}/902501", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"{Sites}/902504", Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // Only the add group may be held, so another add can't join it, but a new update goes as a fresh submission.
    [Fact]
    public async Task After_a_partial_hold_only_a_held_change_type_is_closed_to_new_sites()
    {
        var (factory, client, _) = CaseManagementFactory();
        using var disposeFactory = factory;
        using var disposeClient = client;
        SubmitFails(factory, UpdateGroupRefused());
        await StartMixedRequest(client);
        await client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);

        (await client.PostAsJsonAsync(Sites, new { siteId = 902505 }, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync(Sites, new { siteId = 902502, changeType = UpdateType }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Created);

        // The new update was never sent, so it can be changed and taken out like any other.
        (await client.PatchAsJsonAsync($"{Sites}/902502", new { townOrCity = "Porto" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
        (await client.DeleteAsync($"{Sites}/902502", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // A group that may have been held after a 5xx is proved not held by a later 400: ManagementBe checks for a replay
    // before it validates. So the update can be changed or taken out again.
    [Fact]
    public async Task A_later_refusal_clears_a_group_an_earlier_attempt_may_have_left_held()
    {
        var (factory, client, _) = CaseManagementFactory();
        using var disposeFactory = factory;
        using var disposeClient = client;
        SubmitFails(
            factory,
            new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway),
            new HttpRequestException("refused", null, HttpStatusCode.BadRequest)
        );
        await client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = UpdateType }, Ct);
        await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Recycling" }, Ct);

        await client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);
        factory.FakeChangeRequests.All.Single().Sites.Single().CaseManagementMayHold.Should().BeTrue();
        await client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);

        var stored = factory.FakeChangeRequests.All.Single();
        stored.Sites.Single().CaseManagementMayHold.Should().BeFalse();
        stored.SentToCaseManagement.Should().BeFalse();
        // Its only site, so taking it out deletes the draft.
        (await client.DeleteAsync($"{Sites}/902501", Ct)).IsSuccessStatusCode.Should().BeTrue();
    }

    // A held site is replayed as first sent and can't be changed or removed, so submit doesn't hold it to rules it can
    // no longer meet: here, an ORS that stopped being accredited after the update was sent.
    [Fact]
    public async Task A_held_update_whose_ORS_is_no_longer_accredited_doesnt_block_the_resubmit()
    {
        var (factory, client, seeded) = CaseManagementFactory();
        using var disposeFactory = factory;
        using var disposeClient = client;
        SubmitFails(factory, new CaseWorkingApiTimeoutException("timed out", new TimeoutException()));
        await client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = UpdateType }, Ct);
        await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Recycling" }, Ct);
        await client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);
        seeded.OverseasSites!.Sites.RemoveAll(s => s.SiteId == 902501);
        factory
            .MockCaseWorkingAdapter.SubmitChangeRequestAsync(
                Arg.Any<AccreditationChangeRequest>(),
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(
                new ChangeRequestSubmissionResult(
                    [new SiteWorkItemResult(902501, "IYC-ORS-2026-00011", Guid.NewGuid())],
                    ["SUB-2026-0011"]
                )
            );

        var resubmitted = await client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);

        resubmitted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // A held update is replayed as Case Management first received it, so submit doesn't rebase it on the ORS.
    [Fact]
    public async Task A_held_update_is_not_rebased_at_resubmit()
    {
        var (factory, client, seeded) = CaseManagementFactory();
        using var disposeFactory = factory;
        using var disposeClient = client;
        SubmitFails(
            factory,
            new CaseWorkingApiTimeoutException("timed out", new TimeoutException()),
            new CaseWorkingApiTimeoutException("timed out", new TimeoutException())
        );
        await client.PostAsJsonAsync(Sites, new { siteId = 902501, changeType = UpdateType }, Ct);
        await client.PatchAsJsonAsync($"{Sites}/902501", new { siteName = "Gdansk Recycling" }, Ct);
        await client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);
        ChangeRequestRules.FindSite(seeded, 902501)!.ContactName = "New Manager";

        await client.PostAsJsonAsync(SubmitUrl, Declaration(null), Ct);

        var site = factory.FakeChangeRequests.All.Single().Sites.Single();
        site.Previous!.ContactName.Should().Be("Site Manager");
        site.Proposed!.ContactName.Should().Be("Site Manager");
    }

    [Fact]
    public async Task A_held_add_takes_no_new_BES_evidence()
    {
        var (factory, client, _) = CaseManagementFactory();
        using var disposeFactory = factory;
        using var disposeClient = client;
        SubmitFails(factory, new CaseWorkingApiTimeoutException("timed out", new TimeoutException()));
        await client.PostAsJsonAsync(Sites, new { siteId = 902504 }, Ct);
        await client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);

        (await client.PostAsJsonAsync($"{Sites}/902504/bes-evidence/files", new { fileUploadId = "file-1" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"{Sites}/902504/bes-evidence/files/file-1", Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.Conflict);
    }

    // A defect thrown before anything was sent (here, by the adapter for the first group) leaves Case Management
    // holding nothing, so nothing is fixed and the request can still be changed.
    [Fact]
    public async Task A_defect_before_anything_was_sent_fixes_nothing()
    {
        var (factory, client, _) = CaseManagementFactory();
        using var disposeFactory = factory;
        using var disposeClient = client;
        SubmitFails(factory, new InvalidOperationException("Update item for site 902501 has no ORS on the accreditation."));
        await StartMixedRequest(client);

        var response = await client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var stored = factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.InProgress);
        stored.SentToCaseManagement.Should().BeFalse();
        stored.Sites.Select(s => s.CaseManagementMayHold).Should().AllBeEquivalentTo(false);
        (await client.DeleteAsync($"{Sites}/902504", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(Sites, new { siteId = 902505 }, Ct)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // AC02: the interim site's country and recycling operations are its own, however the form sent them back.
    [Fact]
    public async Task An_interim_site_saved_unchanged_with_its_codes_reordered_records_no_change()
    {
        await StartUpdate(902501);
        var codes = UpdateOverseasSiteRules.ActiveInterimSite(ChangeRequestRules.FindSite(_accreditation, 902501)!, 902511)!
            .OperationCodes;

        var saved = await _client.PatchAsJsonAsync(
            $"{Sites}/902501/interim-sites/902511",
            Interim("Gdynia Transfer Hub", country: " Poland ", codes: [.. Enumerable.Reverse(codes)]),
            Ct
        );

        saved.StatusCode.Should().Be(HttpStatusCode.OK);
        SiteIn(await Json(saved), 902501).GetProperty("interimSiteChanges").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task A_free_request_keeps_no_payment_reference()
    {
        await StartUpdate(902501);
        await Patch(902501, new { siteName = "Gdansk Recycling" });

        (await _client.PostAsJsonAsync(SubmitUrl, Declaration("PR/PK/EX/500250"), Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        var stored = _factory.FakeChangeRequests.All.Single();
        stored.Status.Should().Be(ChangeRequestStatus.Submitted);
        stored.PaymentReference.Should().BeNull();
    }
}
