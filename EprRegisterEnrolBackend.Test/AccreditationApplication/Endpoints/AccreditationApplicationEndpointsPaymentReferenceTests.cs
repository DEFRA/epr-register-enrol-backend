using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.ReEx;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NSubstitute;
using NSubstitute.ClearExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-000: Submit uses the stored organisation number and nation, looks each up in ReEx only when
// it is not stored, never replaces a stored value, and refuses (503, nothing sent or saved, Error
// log with the ids) when either is still missing. It builds the payment reference itself; the GET
// application response returns the one built from the current values.
public class AccreditationApplicationEndpointsPaymentReferenceTests
    : IClassFixture<AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory>
{
    private const string Org = "org-123";
    private const string RegistrationId = "reg-1";

    private readonly AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory _factory;
    private readonly HttpClient _client;

    public AccreditationApplicationEndpointsPaymentReferenceTests(
        AccreditationApplicationEndpointsValidationLoggingTests.LoggingTestFactory factory
    )
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.FakePersistence.Clear();
        _factory.LogCapture.Clear();
        _factory.MockReExAdapter.ClearSubstitute(ClearOptions.All);
        _factory.MockCaseWorkingAdapter.ClearSubstitute(ClearOptions.All);
        _factory
            .MockCaseWorkingAdapter.SubmitApplicationAsync(
                Arg.Any<AccreditationApplicationModel>(),
                Arg.Any<CancellationToken>()
            )
            .Returns(Task.FromResult(new CaseWorkingSubmissionResult("AP26EA500500", null)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AccreditationApplicationModel SeedStarted(
        int? orgId,
        Nation? nation,
        string? registrationId = RegistrationId,
        string? storedPaymentReference = null
    )
    {
        var app = new AccreditationApplicationModel
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = Org,
            OrgId = orgId,
            Nation = nation,
            RegistrationId = registrationId,
            PaymentReference = storedPaymentReference,
            Year = 2026,
            MaterialType = MaterialType.Steel,
            ApplicationStatus = ApplicationStatus.Started,
        };
        app.Prns.SectionStatus = SectionStatus.Completed;
        app.BusinessPlan.SectionStatus = SectionStatus.Completed;
        app.SamplingPlan.SectionStatus = SectionStatus.Completed;
        _factory.FakePersistence.Seed(app);
        return app;
    }

    private Task<HttpResponseMessage> Submit(AccreditationApplicationModel app, object? body = null) =>
        _client.PostAsJsonAsync(
            $"/api/v1/accreditation-applications/{Org}/{app.Id!.Value}/submit",
            body ?? new { fullName = "John", jobTitle = "Manager", email = "j@x.com" },
            Ct
        );

    private void OrgNumberLookupReturns(ReExResult<int?> result) =>
        _factory
            .MockReExAdapter.GetOrganisationNumberAsync(Org, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    private void NationLookupReturns(ReExResult<Nation> result) =>
        _factory
            .MockReExAdapter.GetNationAsync(Org, RegistrationId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));

    private async Task<AccreditationApplicationModel> Stored(AccreditationApplicationModel app) =>
        (await _factory.FakePersistence.GetByIdAsync(Org, app.Id!.Value.ToString()))!;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct));

    private Task<CaseWorkingSubmissionResult> SentToCaseManagement(Func<AccreditationApplicationModel, bool> match) =>
        _factory
            .MockCaseWorkingAdapter.Received(1)
            .SubmitApplicationAsync(
                Arg.Is<AccreditationApplicationModel>(a => match(a)),
                Arg.Any<CancellationToken>()
            );

    private async Task AssertBlocked(
        HttpResponseMessage response,
        AccreditationApplicationModel app,
        string title,
        string reExOutcome
    )
    {
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await Json(response);
        problem.GetProperty("status").GetInt32().Should().Be(503);
        problem.GetProperty("title").GetString().Should().Be(title);

        await _factory
            .MockCaseWorkingAdapter.DidNotReceiveWithAnyArgs()
            .SubmitApplicationAsync(default!, Ct);

        var stored = await Stored(app);
        stored.ApplicationStatus.Should().Be(ApplicationStatus.Started);
        stored.OrgId.Should().Be(app.OrgId);
        stored.Nation.Should().Be(app.Nation);
        stored.PaymentReference.Should().BeNull();
        stored.Version.Should().Be(app.Version, "nothing is written");

        var error = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Subject;
        error.Message.Should().StartWith("Submit blocked");
        error.Message.Should().Contain($"applicationId={app.Id}");
        error.Message.Should().Contain($"organisationId={Org}");
        error.Message.Should().Contain(reExOutcome);
    }

    // AC1: a stored organisation number and nation are used as they are - ReEx is not asked.
    [Fact]
    public async Task Submit_StoredOrgNumberAndNation_BuildsStoresAndSendsTheReferenceWithoutAReExLookup()
    {
        var app = SeedStarted(orgId: 500500, nation: Nation.England);

        var response = await Submit(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockReExAdapter.DidNotReceiveWithAnyArgs()
            .GetOrganisationNumberAsync(default!, Ct);
        await _factory.MockReExAdapter.DidNotReceiveWithAnyArgs().GetNationAsync(default!, default!, Ct);
        await SentToCaseManagement(a =>
            a.OrgId == 500500 && a.Nation == Nation.England && a.PaymentReference == "PR/PK/REP/500500"
        );
        var stored = await Stored(app);
        stored.ApplicationStatus.Should().Be(ApplicationStatus.Submitted);
        stored.OrgId.Should().Be(500500);
        stored.PaymentReference.Should().Be("PR/PK/REP/500500");
    }

    // AC1: a stored organisation number is never replaced, even when ReEx would now answer
    // differently (or would fail).
    [Fact]
    public async Task Submit_StoredOrgNumber_IsNeverReplacedByAReExValue()
    {
        var app = SeedStarted(orgId: 500500, nation: Nation.England);
        OrgNumberLookupReturns(ReExResult<int?>.Fail(new ReExError(ReExErrorKind.ServerError), 500));

        var response = await Submit(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Stored(app)).OrgId.Should().Be(500500);
    }

    // AC1: none stored - ReEx is asked once and the number it gives is sent and saved.
    [Fact]
    public async Task Submit_NoStoredOrgNumber_LooksItUpOnceAndSavesIt()
    {
        var app = SeedStarted(orgId: null, nation: Nation.England);
        OrgNumberLookupReturns(ReExResult<int?>.Success(500600, 200));

        var response = await Submit(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await _factory
            .MockReExAdapter.Received(1)
            .GetOrganisationNumberAsync(Org, Arg.Any<CancellationToken>());
        await SentToCaseManagement(a => a.OrgId == 500600 && a.PaymentReference == "PR/PK/REP/500600");
        var stored = await Stored(app);
        stored.OrgId.Should().Be(500600);
        stored.PaymentReference.Should().Be("PR/PK/REP/500600");
    }

    // AC2: no organisation number stored and ReEx can't give one - failure, not found, or an
    // organisation with no orgId recorded.
    public static TheoryData<string, int?, string> OrgNumberFailures =>
        new()
        {
            { "ServerError", 500, "ReEx status=500 kind=ServerError" },
            { "NotFound", 404, "ReEx status=404 kind=NotFound" },
            { "Timeout", null, "ReEx status=(none) kind=Timeout" },
            { "NoOrgIdRecorded", 200, "ReEx status=200 returned no value" },
        };

    [Theory]
    [MemberData(nameof(OrgNumberFailures))]
    public async Task Submit_NoOrgNumberAvailable_Returns503SendsNothingSavesNothingAndLogsError(
        string outcome,
        int? status,
        string reExOutcome
    )
    {
        var app = SeedStarted(orgId: null, nation: Nation.England);
        OrgNumberLookupReturns(
            outcome == "NoOrgIdRecorded"
                ? ReExResult<int?>.Success(null, 200)
                : ReExResult<int?>.Fail(new ReExError(Enum.Parse<ReExErrorKind>(outcome)), status)
        );

        var response = await Submit(app);

        await AssertBlocked(response, app, "Organisation number unavailable", reExOutcome);
    }

    // C2: building the reference needs the nation. None stored - ReEx's lookup is used.
    [Fact]
    public async Task Submit_NoStoredNation_LooksItUpAndBuildsThatNationsReference()
    {
        var app = SeedStarted(orgId: 500500, nation: null);
        NationLookupReturns(ReExResult<Nation>.Success(Nation.Wales, 200));

        var response = await Submit(app);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await SentToCaseManagement(a => a.Nation == Nation.Wales && a.PaymentReference == "PREX/500500");
        var stored = await Stored(app);
        stored.Nation.Should().Be(Nation.Wales);
        stored.PaymentReference.Should().Be("PREX/500500");
    }

    // C2: no nation and ReEx can't give one - never defaulted to England.
    [Fact]
    public async Task Submit_NoNationAvailable_Returns503SendsNothingSavesNothingAndLogsError()
    {
        var app = SeedStarted(orgId: 500500, nation: null);
        NationLookupReturns(
            ReExResult<Nation>.Fail(new ReExError(ReExErrorKind.NotFound, "no registration"), 404)
        );

        var response = await Submit(app);

        await AssertBlocked(response, app, "Nation unavailable", "ReEx status=404 kind=NotFound");
    }

    // C2: no nation and no registration to look it up from.
    [Fact]
    public async Task Submit_NoNationAndNoRegistrationId_Returns503WithoutALookup()
    {
        var app = SeedStarted(orgId: 500500, nation: null, registrationId: null);

        var response = await Submit(app);

        await AssertBlocked(
            response,
            app,
            "Nation unavailable",
            "not looked up: the application has no registrationId"
        );
        await _factory.MockReExAdapter.DidNotReceiveWithAnyArgs().GetNationAsync(default!, default!, Ct);
    }

    // AC3: an old client (or a direct API call) still sending paymentReference has it ignored -
    // the built reference is what is stored and sent.
    [Fact]
    public async Task Submit_CallerSendsAPaymentReference_ItIsIgnored()
    {
        var app = SeedStarted(orgId: 500500, nation: Nation.Scotland);

        var response = await Submit(
            app,
            new
            {
                fullName = "John",
                jobTitle = "Manager",
                email = "j@x.com",
                paymentReference = "PR/PK/REP/null",
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await SentToCaseManagement(a => a.PaymentReference == "E800 81581/500500");
        (await Stored(app)).PaymentReference.Should().Be("E800 81581/500500");
    }

    // --- GET application (AC4/AC5 backend half) ---

    private async Task<JsonElement> GetApplication(AccreditationApplicationModel app)
    {
        var response = await _client.GetAsync(
            $"/api/v1/accreditation-applications/{Org}/{app.Id!.Value}",
            Ct
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await Json(response);
    }

    [Fact]
    public async Task Get_ReturnsTheReferenceBuiltFromTheStoredOrgNumberAndNation()
    {
        var app = SeedStarted(orgId: 500500, nation: Nation.NorthernIreland);

        var body = await GetApplication(app);

        body.GetProperty("paymentReference").GetString().Should().Be("NI/PR/REEX/500500");
        _factory.LogCapture.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
    }

    // AC5: null when the organisation number can't be found - never a reference ending "/null"
    // and never another reference in its place.
    [Fact]
    public async Task Get_NoOrgNumberAvailable_ReturnsANullPaymentReference()
    {
        var app = SeedStarted(orgId: null, nation: Nation.England);
        app.ApplicationReference = "AP26EA";
        OrgNumberLookupReturns(ReExResult<int?>.Fail(new ReExError(ReExErrorKind.ServerError), 500));

        var body = await GetApplication(app);

        body.GetProperty("paymentReference").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Get_NoNationAvailable_ReturnsANullPaymentReference()
    {
        var app = SeedStarted(orgId: 500500, nation: null);
        NationLookupReturns(ReExResult<Nation>.Fail(new ReExError(ReExErrorKind.ServerError), 500));

        var body = await GetApplication(app);

        body.GetProperty("paymentReference").ValueKind.Should().Be(JsonValueKind.Null);
    }

    // C4: a stored reference that differs from the built one (e.g. "/null" captured from the
    // frontend before this change) is logged at Error for migration and the built one returned.
    [Fact]
    public async Task Get_StoredReferenceDiffers_ReturnsTheBuiltOneAndLogsError()
    {
        var app = SeedStarted(
            orgId: 500500,
            nation: Nation.England,
            storedPaymentReference: "PR/PK/REP/null"
        );

        var body = await GetApplication(app);

        body.GetProperty("paymentReference").GetString().Should().Be("PR/PK/REP/500500");
        var error = _factory
            .LogCapture.Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error)
            .Subject;
        error.Message.Should().Contain($"applicationId={app.Id}");
        error.Message.Should().Contain($"organisationId={Org}");
        error.Message.Should().Contain("PR/PK/REP/null");
    }
}
