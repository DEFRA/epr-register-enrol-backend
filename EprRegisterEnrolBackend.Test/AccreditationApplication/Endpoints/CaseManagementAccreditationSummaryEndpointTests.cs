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

    [Theory]
    [InlineData(null, true, "exporter")]
    [InlineData("", true, "exporter")]
    [InlineData("  ", false, "reprocessor")]
    [InlineData("Exporter", false, "exporter")]
    public async Task AnApplicationWithoutAProcessingTypeString_AnswersItFromTheExporterFlag(
        string? wasteProcessingType,
        bool isExporter,
        string expected
    )
    {
        var app = ApprovedExporter();
        app.WasteProcessingType = wasteProcessingType;
        app.IsExporter = isExporter;
        _factory.FakePersistence.Seed(app);

        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("wasteProcessingType").GetString().Should().Be(expected);
    }

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

    private static OverseasSiteModel Site(int siteId, string? orsId, string name, bool selected = true) =>
        new()
        {
            SiteId = siteId,
            OrsId = orsId,
            SiteName = name,
            Selected = selected,
        };

    private async Task<JsonElement> GetBody(AccreditationApplicationModel app)
    {
        _factory.FakePersistence.Seed(app);
        var response = await _client.GetAsync(Url(app.OrganisationId, app.ApplicationId!), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task AnOverseasSite_AnswersItsDetails()
    {
        var app = ApprovedExporter();
        app.OverseasSites = new() { Sites =
        [
            new OverseasSiteModel
            {
                SiteId = 7,
                OrsId = "001",
                SiteName = "Speira GmbH",
                SiteAddress = "Flat line",
                AddressLine1 = "1 Test Strasse",
                AddressLine2 = "Block B",
                TownOrCity = "Bremen",
                Country = "Germany",
                Coordinates = "51.847,7.123",
                ContactName = "Hans Contact",
                ContactEmail = "hans@example.de",
                ContactPhone = "+49 123",
                OperationCodes = ["R4", "R5"],
                Code1 = "B1010",
                Code2 = "B1020",
                Code3 = "B1030",
                RepatriatedLoads = "As per article 18",
                ConditionsOfExport = true,
                IsEu = true,
                IsOecd = false,
                Selected = true,
            },
        ] };

        var body = await GetBody(app);

        var site = body.GetProperty("overseasSites")[0];
        site.GetProperty("siteId").GetInt32().Should().Be(7);
        site.GetProperty("orsId").GetString().Should().Be("001");
        site.GetProperty("siteName").GetString().Should().Be("Speira GmbH");
        site.GetProperty("siteAddress").GetString().Should().Be("Flat line");
        site.GetProperty("addressLine1").GetString().Should().Be("1 Test Strasse");
        site.GetProperty("addressLine2").GetString().Should().Be("Block B");
        site.GetProperty("townOrCity").GetString().Should().Be("Bremen");
        site.GetProperty("country").GetString().Should().Be("Germany");
        site.GetProperty("coordinates").GetString().Should().Be("51.847,7.123");
        site.GetProperty("contactName").GetString().Should().Be("Hans Contact");
        site.GetProperty("contactEmail").GetString().Should().Be("hans@example.de");
        site.GetProperty("contactPhone").GetString().Should().Be("+49 123");
        site.GetProperty("operationCodes").EnumerateArray().Select(e => e.GetString()).Should().Equal("R4", "R5");
        site.GetProperty("code1").GetString().Should().Be("B1010");
        site.GetProperty("code2").GetString().Should().Be("B1020");
        site.GetProperty("code3").GetString().Should().Be("B1030");
        site.GetProperty("repatriatedLoads").GetString().Should().Be("As per article 18");
        site.GetProperty("conditionsOfExport").GetBoolean().Should().BeTrue();
        site.GetProperty("isEu").GetBoolean().Should().BeTrue();
        site.GetProperty("isOecd").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AnOverseasSite_CarriesOnlyTheAllowListedFields_NoNewSiteMarkers()
    {
        var app = ApprovedExporter();
        app.OverseasSites = new() { Sites = [Site(1, "001", "Speira GmbH")] };
        app.OverseasSites.Sites[0].IsNewSite = true;
        app.OverseasSites.Sites[0].RegisteredNowAccredited = true;
        app.OverseasSites.Sites[0].ValidFrom = "2026-01-01T00:00:00Z";

        var body = await GetBody(app);

        body.GetProperty("overseasSites")[0]
            .EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo(
                "siteId",
                "orsId",
                "siteName",
                "siteAddress",
                "addressLine1",
                "addressLine2",
                "townOrCity",
                "country",
                "coordinates",
                "contactName",
                "contactEmail",
                "contactPhone",
                "operationCodes",
                "code1",
                "code2",
                "code3",
                "repatriatedLoads",
                "conditionsOfExport",
                "isEu",
                "isOecd",
                "interimSites",
                "besEvidence"
            );
        body.GetRawText()
            .Should()
            .NotContainEquivalentOf("isNewSite")
            .And.NotContainEquivalentOf("registeredNowAccredited")
            .And.NotContainEquivalentOf("validFrom")
            .And.NotContainEquivalentOf("selected");
    }

    private static InterimSiteModel Interim(int siteId, string name, DateTime? removedAt = null) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = $"I-{siteId}",
            Country = "Germany",
            SiteName = name,
            AddressLine1 = "2 Interim Strasse",
            AddressLine2 = "Unit 4",
            TownOrCity = "Hamburg",
            StateOrRegion = "Hamburg",
            Postcode = "20095",
            ContactName = "Ines Interim",
            ContactEmail = "ines@example.de",
            ContactPhone = "+49 456",
            OperationCodes = ["R12", "R3"],
            IsNewSite = true,
            CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            RemovedAt = removedAt,
        };

    [Fact]
    public async Task AnInterimSite_AnswersItsDetails_AndNothingElse()
    {
        var app = ApprovedExporter();
        var site = Site(1, "001", "Speira GmbH");
        site.InterimSites = [Interim(11, "Hamburg Interim")];
        app.OverseasSites = new() { Sites = [site] };

        var body = await GetBody(app);

        var interim = body.GetProperty("overseasSites")[0].GetProperty("interimSites")[0];
        interim.GetProperty("siteId").GetInt32().Should().Be(11);
        interim.GetProperty("siteNumber").GetString().Should().Be("I-11");
        interim.GetProperty("siteName").GetString().Should().Be("Hamburg Interim");
        interim.GetProperty("addressLine1").GetString().Should().Be("2 Interim Strasse");
        interim.GetProperty("addressLine2").GetString().Should().Be("Unit 4");
        interim.GetProperty("townOrCity").GetString().Should().Be("Hamburg");
        interim.GetProperty("stateOrRegion").GetString().Should().Be("Hamburg");
        interim.GetProperty("postcode").GetString().Should().Be("20095");
        interim.GetProperty("country").GetString().Should().Be("Germany");
        interim.GetProperty("contactName").GetString().Should().Be("Ines Interim");
        interim.GetProperty("contactEmail").GetString().Should().Be("ines@example.de");
        interim.GetProperty("contactPhone").GetString().Should().Be("+49 456");
        interim.GetProperty("operationCodes").EnumerateArray().Select(e => e.GetString()).Should().Equal("R12", "R3");
        interim
            .EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo(
                "siteId",
                "siteNumber",
                "siteName",
                "addressLine1",
                "addressLine2",
                "townOrCity",
                "stateOrRegion",
                "postcode",
                "country",
                "contactName",
                "contactEmail",
                "contactPhone",
                "operationCodes"
            );
        body.GetRawText()
            .Should()
            .NotContainEquivalentOf("isNewSite")
            .And.NotContainEquivalentOf("removedAt")
            .And.NotContainEquivalentOf("createdAt")
            .And.NotContainEquivalentOf("statusHistory");
    }

    [Fact]
    public async Task WithdrawnInterimSites_AreNeverAnswered_SoNeverTheFirstOne()
    {
        var app = ApprovedExporter();
        var withdrawn = Site(1, "001", "Only withdrawn");
        withdrawn.InterimSites = [Interim(11, "Gone", removedAt: DateTime.UtcNow)];
        var mixed = Site(2, "002", "Withdrawn first");
        mixed.InterimSites =
        [
            Interim(21, "Gone first", removedAt: DateTime.UtcNow),
            Interim(22, "Active one"),
            Interim(23, "Active two"),
        ];
        app.OverseasSites = new() { Sites = [withdrawn, mixed] };

        var body = await GetBody(app);

        var sites = body.GetProperty("overseasSites");
        sites[0].GetProperty("interimSites").GetArrayLength().Should().Be(0);
        sites[1]
            .GetProperty("interimSites")
            .EnumerateArray()
            .Select(i => i.GetProperty("siteName").GetString())
            .Should()
            .Equal("Active one", "Active two");
    }

    [Fact]
    public async Task ASiteWithNoInterimSites_AnswersAnEmptyList()
    {
        var app = ApprovedExporter();
        app.OverseasSites = new() { Sites = [Site(1, "001", "No interim")] };

        var body = await GetBody(app);

        body.GetProperty("overseasSites")[0].GetProperty("interimSites").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ALegacySiteWithOnlyTheSingularInterimSite_StillAnswersIt()
    {
        var app = ApprovedExporter();
        var site = Site(1, "001", "Legacy");
        site.InterimSite = Interim(11, "Legacy interim");
        app.OverseasSites = new() { Sites = [site] };

        var body = await GetBody(app);

        body.GetProperty("overseasSites")[0]
            .GetProperty("interimSites")
            .EnumerateArray()
            .Select(i => i.GetProperty("siteName").GetString())
            .Should()
            .Equal("Legacy interim");
    }

    [Fact]
    public async Task OnlySelectedSitesAreAnswered_ARequestedButUnselectedOrsIsAbsent()
    {
        var app = ApprovedExporter();
        app.OverseasSites = new() { Sites =
        [
            Site(1, "001", "Granted site"),
            Site(2, "002", "Requested, not yet granted", selected: false),
            Site(3, "003", "Another granted site"),
        ] };

        var body = await GetBody(app);

        body.GetProperty("overseasSites")
            .EnumerateArray()
            .Select(s => s.GetProperty("siteName").GetString())
            .Should()
            .Equal("Granted site", "Another granted site");
    }

    [Fact]
    public async Task Sites_AreOrderedByOrsIdThenSiteId_AsTheAccreditationApiOrdersThem()
    {
        var app = ApprovedExporter();
        app.OverseasSites = new() { Sites =
        [
            Site(9, "002", "C"),
            Site(5, "001", "B"),
            Site(2, "002", "A"),
            Site(3, "001", "D"),
        ] };

        var body = await GetBody(app);

        body.GetProperty("overseasSites")
            .EnumerateArray()
            .Select(s => s.GetProperty("siteId").GetInt32())
            .Should()
            .Equal(3, 5, 2, 9);
    }

    [Fact]
    public async Task AnApplicationWithNoSites_AnswersAnEmptyList()
    {
        var body = await GetBody(ApprovedExporter());

        body.GetProperty("overseasSites").GetArrayLength().Should().Be(0);
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

    private static AccreditationApplicationFile SamplingFile(
        string fileId,
        FileScanStatus scanStatus = FileScanStatus.Clean
    ) =>
        new()
        {
            FileId = fileId,
            Filename = $"{fileId}.pdf",
            ContentType = "application/pdf",
            UploadedByUserId = "user-SECRET",
            ScanStatus = scanStatus,
            S3Key = $"sampling-plans/{fileId}.pdf",
            S3Bucket = "plans-bucket",
        };

    private static BesEvidenceFileModel BesFile(string fileId, string? scanStatus = "Clean") =>
        new()
        {
            FileId = fileId,
            Filename = $"{fileId}.pdf",
            ContentType = "application/pdf",
            ScanStatus = scanStatus,
            BesEvidenceValidFromDate = "2026-01-01",
            BesEvidenceExpiryDate = "2027-12-31",
            UploadedBy = "uploader-SECRET",
            S3Key = $"bes/{fileId}.pdf",
            S3Bucket = "bes-bucket",
        };

    [Fact]
    public async Task TheSamplingPlan_AnswersEachFileWithWhatADownloadNeeds_AndNothingElse()
    {
        var app = ApprovedExporter();
        app.SamplingPlan.Files = [SamplingFile("sp-1"), SamplingFile("sp-2", FileScanStatus.Pending)];

        var body = await GetBody(app);

        var files = body.GetProperty("samplingPlan").GetProperty("files").EnumerateArray().ToList();
        files.Should().HaveCount(2);
        files[0]
            .EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo("fileId", "filename", "contentType", "scanStatus", "s3Key", "s3Bucket");
        files[0].GetProperty("fileId").GetString().Should().Be("sp-1");
        files[0].GetProperty("filename").GetString().Should().Be("sp-1.pdf");
        files[0].GetProperty("contentType").GetString().Should().Be("application/pdf");
        files[0].GetProperty("scanStatus").GetString().Should().Be("Clean");
        files[0].GetProperty("s3Key").GetString().Should().Be("sampling-plans/sp-1.pdf");
        files[0].GetProperty("s3Bucket").GetString().Should().Be("plans-bucket");
        files[1].GetProperty("scanStatus").GetString().Should().Be("Pending");
        body.GetRawText().Should().NotContain("SECRET");
    }

    [Fact]
    public async Task AnApplicationWithNoSamplingPlanFiles_AnswersAnEmptyList()
    {
        var body = await GetBody(ApprovedExporter());

        body.GetProperty("samplingPlan").GetProperty("files").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ASitesBesEvidence_AnswersEachFileWithWhatADownloadNeeds_AndNothingElse()
    {
        var app = ApprovedExporter();
        var site = Site(1, "001", "Speira GmbH");
        site.BesEvidence = new BesEvidenceModel
        {
            BesEvidenceUploads = [BesFile("bes-1"), BesFile("bes-2", scanStatus: null)],
        };
        app.OverseasSites = new() { Sites = [site] };

        var body = await GetBody(app);

        var files = body.GetProperty("overseasSites")[0]
            .GetProperty("besEvidence")
            .GetProperty("files")
            .EnumerateArray()
            .ToList();
        files.Should().HaveCount(2);
        files[0]
            .EnumerateObject()
            .Select(p => p.Name)
            .Should()
            .BeEquivalentTo("fileId", "filename", "contentType", "scanStatus", "s3Key", "s3Bucket");
        files[0].GetProperty("fileId").GetString().Should().Be("bes-1");
        files[0].GetProperty("scanStatus").GetString().Should().Be("Clean");
        files[0].GetProperty("s3Key").GetString().Should().Be("bes/bes-1.pdf");
        files[0].GetProperty("s3Bucket").GetString().Should().Be("bes-bucket");
        // No scan result is not a clean one: it is answered as Pending.
        files[1].GetProperty("scanStatus").GetString().Should().Be("Pending");
        body.GetRawText().Should().NotContain("SECRET").And.NotContain("2027-12-31");
    }

    [Fact]
    public async Task ASiteWithNoBesEvidence_AnswersAnEmptyList()
    {
        var app = ApprovedExporter();
        var withEvidence = Site(1, "001", "A");
        var withoutObject = Site(2, "002", "B");
        withoutObject.BesEvidence = null;
        withEvidence.BesEvidence = new BesEvidenceModel();
        app.OverseasSites = new() { Sites = [withEvidence, withoutObject] };

        var body = await GetBody(app);

        foreach (var site in body.GetProperty("overseasSites").EnumerateArray())
        {
            site.GetProperty("besEvidence").GetProperty("files").GetArrayLength().Should().Be(0);
        }
    }

    [Fact]
    public async Task AnUnselectedSitesBesEvidence_IsNeverAnswered()
    {
        var app = ApprovedExporter();
        var requested = Site(2, "002", "Requested ORS", selected: false);
        requested.BesEvidence = new BesEvidenceModel { BesEvidenceUploads = [BesFile("bes-requested")] };
        app.OverseasSites = new() { Sites = [Site(1, "001", "Granted ORS"), requested] };

        var body = await GetBody(app);

        body.GetProperty("overseasSites").GetArrayLength().Should().Be(1);
        body.GetRawText().Should().NotContain("bes-requested");
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
                "businessPlan",
                "samplingPlan",
                "overseasSites"
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
