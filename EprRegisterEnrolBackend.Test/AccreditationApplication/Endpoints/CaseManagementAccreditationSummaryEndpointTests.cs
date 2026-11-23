using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Auth;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Endpoints;

// RA-769: Case Management's read of an accreditation as it stands today, for the In-Year Change case's
// Accreditation summary tab. GET api/v1/accreditation-applications/case-management/{organisationId}/{applicationId}/accreditation-summary,
// on the CaseManagement scheme, answering an allow-listed projection (never the persistence model) for an
// Approved application only.
public class CaseManagementAccreditationSummaryEndpointTests
    : IClassFixture<AccreditationApplicationTestFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AccreditationApplicationTestFactory _factory;
    private readonly HttpClient _client;

    public CaseManagementAccreditationSummaryEndpointTests(AccreditationApplicationTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    internal static string Url(string organisationId, string applicationId) =>
        $"/api/v1/accreditation-applications/case-management/{organisationId}/{applicationId}/accreditation-summary";

    internal static AccreditationApplicationModel ApprovedExporter(
        string organisationId = "org-123",
        ApplicationStatus status = ApplicationStatus.Approved
    ) =>
        new()
        {
            Id = ObjectId.GenerateNewId(),
            OrganisationId = organisationId,
            OrgId = 500500,
            OrganisationName = "Acme Recycling Ltd",
            Year = 2026,
            MaterialType = MaterialType.Glass,
            GlassRecyclingProcess = GlassRecyclingProcess.Remelt,
            IsExporter = true,
            WasteProcessingType = "exporter",
            RegistrationReference = "R26ER5003240164GR",
            AccreditationReference = "A26ER5003240164GR",
            SiteAddress = "1 High Street, Leeds, LS1 1AA",
            CompanyRegisteredAddress = "2 Registered Road, York, YO1 1AA",
            ApplicationStatus = status,
            // Everything below is persistence detail the summary must never return.
            ApplicationReference = "AP-SECRET-REF",
            PaymentReference = "PR/SECRET",
            CaseManagementReference = "CASEREF-SECRET",
            CompaniesHouseNumber = "01234567",
            PermitNumbers = ["PERMIT-SECRET"],
            SubmittedBy = new SubmittedByModel { FullName = "Sam Submitter", JobTitle = "Director" },
            SubmitterContactDetails = new SubmitterContactDetailsModel { Email = "submitter@example.com" },
            Prns = new AccreditationApplicationPrns
            {
                PlannedTonnageBand = PlannedTonnageBand.UpTo5000,
                Authorisers =
                [
                    new PrnsAuthoriser
                    {
                        FullName = "Ada Authoriser",
                        Email = "ada@example.com",
                        IsNew = true,
                    },
                    new PrnsAuthoriser { FullName = "Bob Signer", Email = "bob@example.com" },
                ],
                Versions = [new PrnsSnapshot { VersionedAt = DateTime.UtcNow }],
            },
            BusinessPlan = new AccreditationApplicationBusinessPlan
            {
                NewInfrastructurePercent = 60,
                NewInfrastructureDetail = "A new sorting line",
                PriceSupportPercent = 40,
                PriceSupportDetail = null,
            },
        };

    [Fact]
    public async Task AnApprovedApplication_AnswersItsAccreditationDetails()
    {
        var app = ApprovedExporter();
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("organisationName").GetString().Should().Be("Acme Recycling Ltd");
        body.GetProperty("organisationNumber").GetInt32().Should().Be(500500);
        body.GetProperty("accreditationNumber").GetString().Should().Be("A26ER5003240164GR");
        body.GetProperty("registrationNumber").GetString().Should().Be("R26ER5003240164GR");
        body.GetProperty("accreditationYear").GetInt32().Should().Be(2026);
        body.GetProperty("wasteProcessingType").GetString().Should().Be("exporter");
        body.GetProperty("material").GetString().Should().Be("glass");
        body.GetProperty("glassRecyclingProcess").GetString().Should().Be("glass_re_melt");
        body.GetProperty("siteAddress").GetString().Should().Be("1 High Street, Leeds, LS1 1AA");
        body.GetProperty("companyRegisteredAddress").GetString().Should().Be("2 Registered Road, York, YO1 1AA");
    }

    [Fact]
    public async Task AnApprovedApplication_AnswersItsPrnDetailsAndBusinessPlan()
    {
        var app = ApprovedExporter();
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var prns = body.GetProperty("prns");
        prns.GetProperty("plannedTonnageBand").GetString().Should().Be("UpTo5000");
        var authorisers = prns.GetProperty("authorisers").EnumerateArray().ToList();
        authorisers.Should().HaveCount(2);
        authorisers[0].GetProperty("fullName").GetString().Should().Be("Ada Authoriser");
        authorisers[0].GetProperty("email").GetString().Should().Be("ada@example.com");
        authorisers[1].GetProperty("fullName").GetString().Should().Be("Bob Signer");
        var plan = body.GetProperty("businessPlan");
        plan.GetProperty("newInfrastructurePercent").GetInt32().Should().Be(60);
        plan.GetProperty("newInfrastructureDetail").GetString().Should().Be("A new sorting line");
        plan.GetProperty("priceSupportPercent").GetInt32().Should().Be(40);
    }

    [Fact]
    public async Task APrnsSection_CarriesOnlyTheBandAndAuthorisers_AndAnAuthoriserOnlyItsNameAndEmail()
    {
        var app = ApprovedExporter();
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var prns = body.GetProperty("prns");
        prns.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("plannedTonnageBand", "authorisers");
        prns.GetProperty("authorisers")[0]
            .EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo("fullName", "email");
        body.GetRawText().Should().NotContainEquivalentOf("isNew").And.NotContainEquivalentOf("versions");
    }

    [Fact]
    public async Task AnApplicationWithNoPrnsOrBusinessPlan_AnswersThemEmpty()
    {
        var app = ApprovedExporter();
        app.Prns = new AccreditationApplicationPrns();
        app.BusinessPlan = new AccreditationApplicationBusinessPlan();
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("prns").GetProperty("authorisers").GetArrayLength().Should().Be(0);
        body.GetProperty("prns").GetProperty("plannedTonnageBand").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("businessPlan").GetProperty("newInfrastructurePercent").ValueKind
            .Should()
            .Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task TheResponseCarriesOnlyTheAllowListedFields()
    {
        var app = ApprovedExporter();
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo(
                "organisationName",
                "organisationNumber",
                "accreditationNumber",
                "registrationNumber",
                "accreditationYear",
                "wasteProcessingType",
                "material",
                "glassRecyclingProcess",
                "siteAddress",
                "companyRegisteredAddress",
                "prns",
                "businessPlan"
            );
        var raw = body.GetRawText();
        raw.Should().NotContain("SECRET").And.NotContain("submitter@example.com").And.NotContain("01234567");
    }

    [Fact]
    public async Task AnUnknownApplication_Answers404()
    {
        var response = await _client.GetAsync(
            Url("org-123", ObjectId.GenerateNewId().ToString()),
            Ct
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(ApplicationStatus.Saved)]
    [InlineData(ApplicationStatus.Submitted)]
    [InlineData(ApplicationStatus.DulyMade)]
    [InlineData(ApplicationStatus.Rejected)]
    [InlineData(ApplicationStatus.Withdrawn)]
    public async Task AnApplicationThatIsNotApproved_Answers404(ApplicationStatus status)
    {
        var app = ApprovedExporter(status: status);
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnotherOrganisationsApplication_Answers404()
    {
        var app = ApprovedExporter(organisationId: "org-owner");
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url("org-other", app.ApplicationId!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

// The route outside Development, where the CaseManagement scheme checks management-be's v3 signature: a signed
// request is served, and every other caller (unsigned, the operator frontend's bearer token) is rejected.
public class SignedCaseManagementAccreditationSummaryEndpointTests
    : IClassFixture<SignedCaseManagementTestFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SignedCaseManagementTestFactory _factory;
    private readonly HttpClient _client;

    public SignedCaseManagementAccreditationSummaryEndpointTests(SignedCaseManagementTestFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private HttpRequestMessage Request(AccreditationApplicationModel app, bool signed)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            CaseManagementAccreditationSummaryEndpointTests.Url(app.OrganisationId, app.ApplicationId!)
        );
        if (!signed)
            return request;

        const string userId = "regulator@example.gov.uk";
        const string userName = "Jane Regulator";
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        request.Headers.Add("x-cdp-client-id", SignedCaseManagementTestFactory.ClientId);
        request.Headers.Add("x-cdp-user-id", userId);
        request.Headers.Add("x-cdp-user-name", userName);
        request.Headers.Add(
            "x-cdp-auth-signature",
            CaseManagementAuthenticationHandler.ComputeSignature(
                SignedCaseManagementTestFactory.SharedSecret,
                SignedCaseManagementTestFactory.ClientId,
                userId,
                userName,
                timestamp,
                nonce
            )
        );
        request.Headers.Add("x-cdp-auth-timestamp", timestamp);
        request.Headers.Add("x-cdp-auth-nonce", nonce);
        return request;
    }

    [Fact]
    public async Task ASignedRequest_IsServed()
    {
        var app = CaseManagementAccreditationSummaryEndpointTests.ApprovedExporter();
        _factory.FakePersistence.Seed(app);

        var response = await _client.SendAsync(Request(app, signed: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnUnsignedRequest_Answers401()
    {
        var app = CaseManagementAccreditationSummaryEndpointTests.ApprovedExporter();
        _factory.FakePersistence.Seed(app);

        var response = await _client.SendAsync(Request(app, signed: false), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnotherCallersBearerToken_Answers401()
    {
        var app = CaseManagementAccreditationSummaryEndpointTests.ApprovedExporter();
        _factory.FakePersistence.Seed(app);
        var request = Request(app, signed: false);
        request.Headers.Authorization = new("Bearer", "some-frontend-secret");

        var response = await _client.SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
