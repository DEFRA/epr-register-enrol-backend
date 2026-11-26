using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.DevSeed;
using FluentAssertions;
using NSubstitute;

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

    public UpdateOverseasSiteEndpointTests()
    {
        _factory = new AccreditationChangeTestFactory(submitToCaseManagement: false);
        _factory.FakePersistence.Seed(
            DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearUpdate, Year)
        );
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
        (await StartUpdate(902504)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Add(902501)).StatusCode.Should().Be(HttpStatusCode.Conflict);
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
                    "SUB-2026-0001",
                    [
                        new SiteWorkItemResult(902504, "IYC-ORS-2026-00001", Guid.NewGuid()),
                        new SiteWorkItemResult(902501, "IYC-ORS-2026-00002", Guid.NewGuid()),
                    ]
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
    }
}
