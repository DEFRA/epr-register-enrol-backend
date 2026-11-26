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

namespace EprRegisterEnrolBackend.Test.AccreditationChanges;

// RA-698: the regulator's query on an add-ORS item and the operator's response to it. Org 50019's accreditation
// (DevExporterAccreditationSeedService), whose registered ORS 901914 (Rotterdam) has an interim site, and a Submitted
// request for it and for 901910 (Lyon), each with its own Case Management work item.
public sealed class QueriedItemEndpointsTests : IDisposable
{
    private const int Year = 2026;
    private const int QueriedSiteId = 901914;
    private const int SiblingSiteId = 901910;
    private static readonly string Org = DevExporterOrg.InYearChange.OrganisationId;
    private static readonly string AppId = DevExporterAccreditationSeedService
        .ApplicationIdFor(DevExporterOrg.InYearChange, Year)
        .ToString();
    private static readonly string Base = $"/api/v1/accreditation-changes/{Org}/{AppId}";
    private static readonly DateTime SubmittedAt = new(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AccreditationChangeTestFactory _factory;
    private readonly HttpClient _client;
    private readonly Guid _workItemId = Guid.NewGuid();
    private readonly Guid _siblingWorkItemId = Guid.NewGuid();
    private readonly AccreditationApplicationModel _accreditation;

    public QueriedItemEndpointsTests()
    {
        _factory = new AccreditationChangeTestFactory();
        _accreditation = DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, Year);
        _factory.FakePersistence.Seed(_accreditation);
        _factory.MockCaseWorkingAdapter
            .ResumeChangeRequestItemFromQueryAsync(
                Arg.Any<ChangeRequestSite>(),
                Arg.Any<QuerySubmitterContactDetails>(),
                Arg.Any<ChangeRequestQueryResponse>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(new ResumeFromQueryResult(true));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private OverseasSiteModel QueriedSite => _accreditation.OverseasSites!.Sites.Single(s => s.SiteId == QueriedSiteId);

    private InterimSiteModel InterimSite => QueriedSite.InterimSites.First(s => s.RemovedAt is null);

    private static string QueryUrl(Guid workItemId) =>
        "/" + QueriedItemEndpoints.QueryRoute.Replace("{workItemId}", workItemId.ToString());

    private string ItemUrl(string suffix) => $"{Base}/items/{Item().ItemId}/{suffix}";

    private AccreditationChangeRequest SeedRequest(ChangeItemStatus status = ChangeItemStatus.Submitted, BesEvidenceModel? bes = null)
    {
        var request = new AccreditationChangeRequest
        {
            OrganisationId = Org,
            ApplicationId = AppId,
            MaterialType = MaterialType.Plastic,
            Year = Year,
            Status = ChangeRequestStatus.Submitted,
            SubmittedAt = SubmittedAt,
            SubmittedBy = new SubmittedByModel
            {
                FullName = "Sam Patel",
                JobTitle = "Director",
                Email = "sam@example.com",
            },
            Sites =
            [
                new ChangeRequestSite
                {
                    SiteId = QueriedSiteId,
                    OrsId = "009",
                    SiteName = "Rotterdam",
                    Status = status,
                    BesEvidence = bes,
                    CaseManagementWorkItemId = _workItemId,
                },
                new ChangeRequestSite
                {
                    SiteId = SiblingSiteId,
                    OrsId = "005",
                    SiteName = "Lyon",
                    Status = ChangeItemStatus.Submitted,
                    CaseManagementWorkItemId = _siblingWorkItemId,
                },
            ],
        };
        _factory.FakeChangeRequests.Seed(request);
        return request;
    }

    private Task<HttpResponseMessage> RaiseQuery(string note = "Please confirm the contact details.", Guid? workItemId = null) =>
        _client.PostAsJsonAsync(QueryUrl(workItemId ?? _workItemId), new { note }, Ct);

    private async Task SeedQueried()
    {
        SeedRequest();
        (await RaiseQuery()).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private ChangeRequestSite Item(Guid? workItemId = null) =>
        _factory
            .FakeChangeRequests.All.SelectMany(r => r.Sites)
            .Single(s => s.CaseManagementWorkItemId == (workItemId ?? _workItemId));

    private static object Declaration => new { fullName = "Jane Smith", jobTitle = "Director" };

    // ---------------------------------------------------------------- the query (AC11)

    [Fact]
    public async Task Query_MovesTheItemToQueriedAndRecordsTheNote()
    {
        SeedRequest();

        var response = await RaiseQuery("Check the address.");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(ChangeItemStatus.Queried);
        Item().Query!.QueryNote.Should().Be("Check the address.");
        // The sibling is its own work item.
        Item(_siblingWorkItemId).Status.Should().Be(ChangeItemStatus.Submitted);
        Item(_siblingWorkItemId).Query.Should().BeNull();
    }

    [Theory]
    [InlineData(ChangeItemStatus.DulyMade)]
    [InlineData(ChangeItemStatus.Updated)]
    public async Task Query_IsAcceptedWhileTheRegulatorIsAssessing(ChangeItemStatus status)
    {
        SeedRequest(status);

        (await RaiseQuery()).StatusCode.Should().Be(HttpStatusCode.OK);

        Item().Status.Should().Be(ChangeItemStatus.Queried);
    }

    [Fact]
    public async Task Query_WhileOneIsOpen_Is409AndKeepsTheFirstNote()
    {
        SeedRequest();
        await RaiseQuery("First.");

        (await RaiseQuery("Second.")).StatusCode.Should().Be(HttpStatusCode.Conflict);

        Item().Query!.QueryNote.Should().Be("First.");
    }

    [Theory]
    [InlineData(ChangeItemStatus.Approved)]
    [InlineData(ChangeItemStatus.Rejected)]
    [InlineData(ChangeItemStatus.Withdrawn)]
    public async Task Query_OnAnItemNoLongerUnderAssessment_Is409(ChangeItemStatus status)
    {
        SeedRequest(status);

        (await RaiseQuery()).StatusCode.Should().Be(HttpStatusCode.Conflict);

        Item().Status.Should().Be(status);
    }

    [Fact]
    public async Task Query_ForAnUnknownWorkItem_Is404()
    {
        SeedRequest();

        (await RaiseQuery(workItemId: Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Query_WithoutANote_Is400()
    {
        SeedRequest();

        (await RaiseQuery(note: "  ")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        Item().Status.Should().Be(ChangeItemStatus.Submitted);
    }

    // ---------------------------------------------------------------- the view (AC01, AC05, AC09)

    [Fact]
    public async Task Get_ForAQueriedItem_ExposesTheQueryAndWhatMayBeChanged()
    {
        await SeedQueried();

        var view = await GetView();

        view.GetProperty("hasOpenQuery").GetBoolean().Should().BeTrue();
        var item = view.GetProperty("requests")[0].GetProperty("sites").EnumerateArray().Single(s => s.GetProperty("siteId").GetInt32() == QueriedSiteId);
        var query = item.GetProperty("query");
        query.GetProperty("queryNote").GetString().Should().Be("Please confirm the contact details.");
        Strings(query.GetProperty("editableFields")).Should().Contain(["siteName", "contactEmail", "rejectedLoadsArrangements", "besEvidence"]);
        Strings(query.GetProperty("lockedFields")).Should().Contain(["country", "coordinates", "operationCodes", "conditionsOfExport"]);
        var interim = query.GetProperty("interimSites").EnumerateArray().First();
        Strings(interim.GetProperty("lockedFields")).Should().Equal("country", "operationCodes");
        interim.GetProperty("values").GetProperty("siteName").GetString().Should().Be(InterimSite.SiteName);
    }

    [Fact]
    public async Task Get_WithNoQueriedItem_HasNoOpenQuery()
    {
        SeedRequest();

        (await GetView()).GetProperty("hasOpenQuery").GetBoolean().Should().BeFalse();
    }

    // ---------------------------------------------------------------- changing the ORS (AC03, AC04, AC07)

    [Fact]
    public async Task Patch_ChangesOnlyTheSuppliedEditableFields()
    {
        await SeedQueried();

        var response = await _client.PatchAsJsonAsync(
            ItemUrl("answers"),
            new { siteName = "Rotterdam Renamed BV", contactEmail = "new@example.com" },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var answers = Item().Answers;
        answers.SiteName.Should().Be("Rotterdam Renamed BV");
        answers.ContactEmail.Should().Be("new@example.com");
        answers.ContactName.Should().BeNull();
    }

    [Fact]
    public async Task Patch_BackToTheOrssOwnValue_DropsTheAnswer()
    {
        await SeedQueried();
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = "Changed" }, Ct);

        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = QueriedSite.SiteName }, Ct);

        Item().Answers.SiteName.Should().BeNull();
    }

    [Theory]
    [InlineData("country", "Germany")]
    [InlineData("coordinates", "51.9,4.4")]
    [InlineData("conditionsOfExport", true)]
    public async Task Patch_OfALockedField_Is422AndChangesNothing(string field, object value)
    {
        await SeedQueried();

        var response = await _client.PatchAsJsonAsync(ItemUrl("answers"), new Dictionary<string, object> { [field] = value }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Item().Answers.Country.Should().BeNull();
        Item().Answers.Coordinates.Should().BeNull();
    }

    [Fact]
    public async Task Patch_OfOperationCodesOrBaselCodes_Is422()
    {
        await SeedQueried();

        (await _client.PatchAsJsonAsync(ItemUrl("answers"), new { operationCodes = new[] { "R3" } }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.UnprocessableEntity);
        (await _client.PatchAsJsonAsync(ItemUrl("answers"), new { baselCodes = new[] { "B1010" } }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.UnprocessableEntity);
    }

    [Theory]
    [InlineData("siteName")]
    [InlineData("contactName")]
    [InlineData("contactEmail")]
    [InlineData("rejectedLoadsArrangements")]
    public async Task Patch_BlankingARequiredField_Is400(string field)
    {
        await SeedQueried();

        var response = await _client.PatchAsJsonAsync(ItemUrl("answers"), new Dictionary<string, object> { [field] = " " }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_WithABadEmail_Is400()
    {
        await SeedQueried();

        (await _client.PatchAsJsonAsync(ItemUrl("answers"), new { contactEmail = "not-an-email" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(ChangeItemStatus.Submitted)]
    [InlineData(ChangeItemStatus.Updated)]
    [InlineData(ChangeItemStatus.Approved)]
    public async Task Patch_OnAnItemThatIsNotQueried_Is409(ChangeItemStatus status)
    {
        var request = SeedRequest(status);
        var itemId = request.Sites[0].ItemId;

        var response = await _client.PatchAsJsonAsync($"{Base}/items/{itemId}/answers", new { siteName = "X" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Patch_OnAnotherItemOfTheRequest_Is409()
    {
        await SeedQueried();
        var siblingId = Item(_siblingWorkItemId).ItemId;

        var response = await _client.PatchAsJsonAsync($"{Base}/items/{siblingId}/answers", new { siteName = "X" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Item(_siblingWorkItemId).Answers.SiteName.Should().BeNull();
    }

    [Fact]
    public async Task Patch_OnAnUnknownItem_Is404()
    {
        await SeedQueried();

        (await _client.PatchAsJsonAsync($"{Base}/items/{Guid.NewGuid()}/answers", new { siteName = "X" }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);
    }

    // ---------------------------------------------------------------- interim sites (AC03, AC04)

    [Fact]
    public async Task PatchInterimSite_ChangesItsOwnEditableFields()
    {
        await SeedQueried();
        var interimId = InterimSite.SiteId;

        var response = await _client.PatchAsJsonAsync(
            ItemUrl($"interim-sites/{interimId}/answers"),
            new { siteName = "Bharat Recycling", contactPhone = "+31 99 999 9999" },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var answers = Item().Answers.InterimSites.Single();
        answers.SiteId.Should().Be(interimId);
        answers.SiteName.Should().Be("Bharat Recycling");
        answers.ContactPhone.Should().Be("+31 99 999 9999");
        // The ORS itself is untouched.
        Item().Answers.SiteName.Should().BeNull();
    }

    [Theory]
    [InlineData("country", "Germany")]
    [InlineData("rejectedLoadsArrangements", "Take them back")]
    public async Task PatchInterimSite_OfALockedField_Is422(string field, string value)
    {
        await SeedQueried();

        var response = await _client.PatchAsJsonAsync(
            ItemUrl($"interim-sites/{InterimSite.SiteId}/answers"),
            new Dictionary<string, object> { [field] = value },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Item().Answers.InterimSites.Should().BeEmpty();
    }

    [Fact]
    public async Task PatchInterimSite_OfASiteThatIsNotTheQueriedOrss_Is422()
    {
        await SeedQueried();

        var response = await _client.PatchAsJsonAsync(
            ItemUrl("interim-sites/999999/answers"),
            new { siteName = "Nope" },
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Item().Answers.InterimSites.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- resubmit (AC06, AC07, AC08, AC10)

    [Fact]
    public async Task Resubmit_WithNothingChanged_Is422AndTellsNobody()
    {
        await SeedQueried();

        var response = await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        Item().Status.Should().Be(ChangeItemStatus.Queried);
        await _factory
            .MockCaseWorkingAdapter.DidNotReceiveWithAnyArgs()
            .ResumeChangeRequestItemFromQueryAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Resubmit_WithAChange_SendsItToManagementBeAndMarksTheItemUpdated()
    {
        await SeedQueried();
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = "Rotterdam Renamed BV" }, Ct);
        await _client.PatchAsJsonAsync(
            ItemUrl($"interim-sites/{InterimSite.SiteId}/answers"),
            new { contactName = "Piet Renamed" },
            Ct
        );

        var response = await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Item().Status.Should().Be(ChangeItemStatus.Updated);
        var submission = Item().Query!.QuerySubmissions.Single();
        submission.FullName.Should().Be("Jane Smith");
        submission.JobTitle.Should().Be("Director");
        submission.ChangedFields.Should().BeEquivalentTo(["siteName", $"interimSites[{InterimSite.SiteId}].contactName"]);
        await _factory
            .MockCaseWorkingAdapter.Received(1)
            .ResumeChangeRequestItemFromQueryAsync(
                Arg.Is<ChangeRequestSite>(i => i.ItemId == Item().ItemId),
                Arg.Is<QuerySubmitterContactDetails>(c => c.FullName == "Jane Smith" && c.Role == "Director"),
                Arg.Is<ChangeRequestQueryResponse>(r =>
                    r.Site["siteName"] == "Rotterdam Renamed BV"
                    && r.InterimSites.Count == 1
                    && r.InterimSites[0].Changes["contactName"] == "Piet Renamed"
                ),
                Arg.Any<CancellationToken>()
            );
        // Sibling items are independent.
        Item(_siblingWorkItemId).Status.Should().Be(ChangeItemStatus.Submitted);
    }

    [Fact]
    public async Task Resubmit_IsOnlyPossibleOnce()
    {
        await SeedQueried();
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = "Changed" }, Ct);
        (await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        Item().Query!.QuerySubmissions.Should().HaveCount(1);
    }

    [Fact]
    public async Task Resubmit_WhenManagementBeFails_Is503AndTheItemStaysQueried()
    {
        await SeedQueried();
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = "Changed" }, Ct);
        _factory.MockCaseWorkingAdapter
            .ResumeChangeRequestItemFromQueryAsync(default!, default!, default!, Ct)
            .ReturnsForAnyArgs(new ResumeFromQueryResult(false));

        var response = await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        Item().Status.Should().Be(ChangeItemStatus.Queried);
        Item().Query!.QuerySubmissions.Should().BeEmpty();
    }

    [Theory]
    [InlineData("", "Director")]
    [InlineData("Jane Smith", "")]
    public async Task Resubmit_WithoutTheDeclarationsNameAndTitle_Is400(string fullName, string jobTitle)
    {
        await SeedQueried();
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = "Changed" }, Ct);

        (await _client.PostAsJsonAsync(ItemUrl("resubmit"), new { fullName, jobTitle }, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SecondQuery_KeepsTheEarlierResponsesAndOnlyCountsNewChanges()
    {
        await SeedQueried();
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = "First change" }, Ct);
        await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct);
        (await RaiseQuery("Again.")).StatusCode.Should().Be(HttpStatusCode.OK);
        // The same value again is no change since the second query.
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { siteName = "First change" }, Ct);

        (await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct))
            .StatusCode.Should()
            .Be(HttpStatusCode.UnprocessableEntity);
        await _client.PatchAsJsonAsync(ItemUrl("answers"), new { contactName = "Piet Renamed" }, Ct);
        (await _client.PostAsJsonAsync(ItemUrl("resubmit"), Declaration, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var submissions = Item().Query!.QuerySubmissions;
        submissions.Should().HaveCount(2);
        submissions[1].ChangedFields.Should().Equal("contactName");
    }

    // ---------------------------------------------------------------- BES evidence (AC04, AC12)

    [Fact]
    public async Task DeleteBesFile_UploadedBeforeTheQuery_Is422AndKeepsIt()
    {
        SeedRequest(
            bes: new BesEvidenceModel
            {
                BesEvidenceUploads = [new BesEvidenceFileModel { FileId = "old-file", Filename = "old.pdf", S3Key = "k" }],
            }
        );
        await RaiseQuery();

        var response = await _client.DeleteAsync(ItemUrl("bes-evidence/files/old-file"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    // ---------------------------------------------------------------- helpers

    private async Task<JsonElement> GetView()
    {
        var response = await _client.GetAsync(Base, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static List<string> Strings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString()!).ToList();
}
