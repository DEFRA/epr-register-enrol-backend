using System.Text.Json;
using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

public class AccreditationApiMapperTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static AccreditationApplicationModel Application(
        bool exporter = false,
        MaterialType material = MaterialType.Plastic,
        Action<AccreditationApplicationModel>? configure = null
    )
    {
        var application = new AccreditationApplicationModel
        {
            OrganisationId = "reex-internal-object-id",
            OrgId = 500027,
            OrganisationName = "Egger (UK) Limited",
            Year = 2027,
            MaterialType = material,
            ApplicationStatus = ApplicationStatus.Approved,
            RegistrationReference = "R27ER5000270036PL",
            AccreditationReference = "A27ER5000270063PL",
            ApplicationReference = "RA-000000042",
            CaseManagementWorkItemId = Guid.NewGuid(),
            IsExporter = exporter,
            WasteProcessingType = exporter ? "exporter" : "reprocessor",
            SiteAddress = "Anick Grange Road, Hexham, NE46 4JS",
        };
        configure?.Invoke(application);
        return application;
    }

    private static JsonElement Accreditation(AccreditationApplicationModel application) =>
        JsonSerializer.SerializeToElement(AccreditationApiMapper.ToAccreditation(application), Web);

    [Fact]
    public void Reprocessor_CarriesItsIdentifyingFields()
    {
        var json = Accreditation(Application());

        json.GetProperty("accreditationReference").GetString().Should().Be("A27ER5000270063PL");
        json.GetProperty("registrationNumber").GetString().Should().Be("R27ER5000270036PL");
        json.GetProperty("year").GetInt32().Should().Be(2027);
        json.GetProperty("status").GetString().Should().Be("approved");
        json.GetProperty("material").GetString().Should().Be("Plastic");
        json.GetProperty("wasteProcessingType").GetString().Should().Be("reprocessor");
        json.GetProperty("siteAddress").GetProperty("fullAddress").GetString()
            .Should().Be("Anick Grange Road, Hexham, NE46 4JS");
        json.TryGetProperty("overseasSites", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(GlassRecyclingProcess.Remelt, "glass_re_melt")]
    [InlineData(GlassRecyclingProcess.Other, "glass_other")]
    public void Glass_CarriesItsRecyclingProcess(GlassRecyclingProcess process, string expected)
    {
        var json = Accreditation(
            Application(material: MaterialType.Glass, configure: a => a.GlassRecyclingProcess = process)
        );

        json.GetProperty("material").GetString().Should().Be("Glass");
        json.GetProperty("glassRecyclingProcess").GetString().Should().Be(expected);
    }

    [Fact]
    public void NonGlass_HasNoRecyclingProcess_EvenIfOneWasStored()
    {
        var json = Accreditation(
            Application(configure: a => a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt)
        );

        json.TryGetProperty("glassRecyclingProcess", out _).Should().BeFalse();
    }

    [Fact]
    public void Exporter_HasNoSiteAddress_AndListsItsOrsIds()
    {
        var json = Accreditation(
            Application(
                exporter: true,
                configure: a =>
                    a.OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites =
                        [
                            new OverseasSiteModel { SiteId = 2, OrsId = "002", SiteName = "B" },
                            new OverseasSiteModel { SiteId = 1, OrsId = "001", SiteName = "A" },
                            new OverseasSiteModel { SiteId = 3, OrsId = null, SiteName = "No id yet" },
                        ],
                    }
            )
        );

        json.GetProperty("wasteProcessingType").GetString().Should().Be("exporter");
        json.TryGetProperty("siteAddress", out _).Should().BeFalse();
        json.GetProperty("overseasSites").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("001", "002");
    }

    [Fact]
    public void PrnIssuance_CarriesThePlannedTonnageBand_AndTheSignatories()
    {
        var json = Accreditation(
            Application(
                configure: a =>
                {
                    a.Prns.PlannedTonnageBand = PlannedTonnageBand.UpTo5000;
                    a.Prns.Authorisers =
                    [
                        new PrnsAuthoriser { FullName = "Iva Cruickshank", Email = "iva@example.com", IsNew = true },
                    ];
                }
            )
        );

        var prn = json.GetProperty("prnIssuance");
        prn.GetProperty("plannedIssuance").GetString().Should().Be("UpTo5000");
        var signatory = prn.GetProperty("signatories").EnumerateArray().Single();
        signatory.GetProperty("fullName").GetString().Should().Be("Iva Cruickshank");
        signatory.GetProperty("email").GetString().Should().Be("iva@example.com");
        signatory.TryGetProperty("isNew", out _).Should().BeFalse();
    }

    [Fact]
    public void NoTonnageBandYet_LeavesItOut()
    {
        var json = Accreditation(Application());

        json.GetProperty("prnIssuance").TryGetProperty("plannedIssuance", out _).Should().BeFalse();
        json.GetProperty("prnIssuance").GetProperty("signatories").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void BusinessPlan_ListsOnlyTheCategoriesThatWereFilledIn()
    {
        var json = Accreditation(
            Application(
                configure: a =>
                {
                    a.BusinessPlan.NewInfrastructurePercent = 60;
                    a.BusinessPlan.NewInfrastructureDetail = "New sorting line";
                    a.BusinessPlan.CommunicationsPercent = 40;
                }
            )
        );

        var plan = json.GetProperty("businessPlan").EnumerateArray().ToList();
        plan.Select(p => p.GetProperty("description").GetString())
            .Should().Equal("New infrastructure", "Communications");
        plan[0].GetProperty("percentSpent").GetInt32().Should().Be(60);
        plan[0].GetProperty("detailedDescription").GetString().Should().Be("New sorting line");
        plan[1].TryGetProperty("detailedDescription", out _).Should().BeFalse();
    }

    [Fact]
    public void EmptyBusinessPlan_IsLeftOut() =>
        Accreditation(Application()).TryGetProperty("businessPlan", out _).Should().BeFalse();

    [Fact]
    public void SubmitterContact_UsesTitleForTheJobTitle()
    {
        var json = Accreditation(
            Application(
                configure: a =>
                    a.SubmitterContactDetails = new SubmitterContactDetailsModel
                    {
                        FullName = "Iva Cruickshank",
                        Email = "iva@example.com",
                        Phone = "016977 7346",
                        JobTitle = "Sustainability Director",
                    }
            )
        );

        var contact = json.GetProperty("submitterContactDetails");
        contact.GetProperty("title").GetString().Should().Be("Sustainability Director");
        contact.TryGetProperty("jobTitle", out _).Should().BeFalse();
    }

    [Fact]
    public void Accreditation_CarriesNoInternalIds_OrCaseManagementDetails()
    {
        var text = JsonSerializer.Serialize(
            AccreditationApiMapper.ToAccreditation(Application()),
            Web
        );

        text.Should().NotContain("reex-internal-object-id");
        text.Should().NotContain("RA-000000042");
        text.ToLowerInvariant().Should().NotContain("workitem").And.NotContain("version");
    }

    // ---- overseas sites ----

    private static OverseasSiteModel Site(Action<OverseasSiteModel>? configure = null)
    {
        var site = new OverseasSiteModel
        {
            SiteId = 7,
            OrsId = "001",
            SiteName = "Approved Recyclers GmbH",
            SiteAddress = "Hafenstrasse 1, Hamburg, Germany",
            AddressLine1 = "Hafenstrasse 1",
            TownOrCity = "Hamburg",
            Country = "Germany",
            Coordinates = "53.5511, 9.9937",
            ContactName = "Greta Schmidt",
            ContactEmail = "greta@example.com",
            ContactPhone = "+49 40 555 0142",
            OperationCodes = ["R3"],
            Code1 = "A1181",
            RepatriatedLoads = "Returned within 30 days",
            ConditionsOfExport = true,
            IsEu = true,
            IsOecd = true,
            ValidFrom = "2026-03-14T00:00:00.000Z",
            Selected = true,
            IsNewSite = false,
            RegisteredNowAccredited = false,
            BesEvidence = new BesEvidenceModel
            {
                BesEvidenceUploads =
                [
                    new BesEvidenceFileModel
                    {
                        FileId = "f1",
                        Filename = "evidence.pdf",
                        S3Key = "private/key",
                        S3Bucket = "private-bucket",
                    },
                ],
            },
        };
        configure?.Invoke(site);
        return site;
    }

    private static JsonElement OverseasSite(OverseasSiteModel site) =>
        JsonSerializer.SerializeToElement(AccreditationApiMapper.ToOverseasSite(site), Web);

    [Fact]
    public void OverseasSite_CarriesItsDetails_AndItsApproval()
    {
        var json = OverseasSite(Site());

        json.GetProperty("siteId").GetInt32().Should().Be(7);
        json.GetProperty("orsId").GetString().Should().Be("001");
        json.GetProperty("siteName").GetString().Should().Be("Approved Recyclers GmbH");
        json.GetProperty("formattedAddress").GetString().Should().Be("Hafenstrasse 1, Hamburg, Germany");
        var address = json.GetProperty("address");
        address.GetProperty("line1").GetString().Should().Be("Hafenstrasse 1");
        address.GetProperty("town").GetString().Should().Be("Hamburg");
        address.GetProperty("country").GetString().Should().Be("Germany");
        json.GetProperty("coordinates").GetString().Should().Be("53.5511, 9.9937");
        json.GetProperty("operationCodes").EnumerateArray().Select(e => e.GetString()).Should().Equal("R3");
        json.GetProperty("code1").GetString().Should().Be("A1181");
        json.GetProperty("conditionsOfExport").GetBoolean().Should().BeTrue();
        json.GetProperty("isEu").GetBoolean().Should().BeTrue();
        json.GetProperty("isOecd").GetBoolean().Should().BeTrue();
        json.GetProperty("validFrom").GetString().Should().Be("2026-03-14T00:00:00.000Z");
        json.GetProperty("selected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void OverseasSite_NotYetApproved_HasNoValidFrom()
    {
        var json = OverseasSite(Site(s => s.ValidFrom = null));

        json.TryGetProperty("validFrom", out _).Should().BeFalse();
    }

    [Fact]
    public void OverseasSite_ListsOnlyItsActiveInterimSites()
    {
        var json = OverseasSite(
            Site(
                s =>
                    s.InterimSites =
                    [
                        InterimSite(11, removed: false),
                        InterimSite(12, removed: true),
                        InterimSite(10, removed: false),
                    ]
            )
        );

        json.GetProperty("interimSiteIds").EnumerateArray().Select(e => e.GetInt32())
            .Should().Equal(10, 11);
    }

    [Fact]
    public void OverseasSite_DoesNotServeContactsOrEvidenceFiles()
    {
        var text = JsonSerializer.Serialize(AccreditationApiMapper.ToOverseasSite(Site()), Web);

        text.Should().NotContain("Greta Schmidt").And.NotContain("greta@example.com").And.NotContain("+49 40");
        text.ToLowerInvariant().Should().NotContain("contact").And.NotContain("besevidence");
        text.Should().NotContain("private/key").And.NotContain("private-bucket").And.NotContain("evidence.pdf");
    }

    [Fact]
    public void OverseasSite_WithoutAnAddress_HasNoAddressObject()
    {
        var json = OverseasSite(
            Site(
                s =>
                {
                    s.AddressLine1 = null;
                    s.AddressLine2 = null;
                    s.TownOrCity = null;
                    s.Country = null;
                }
            )
        );

        json.TryGetProperty("address", out _).Should().BeFalse();
    }

    private static InterimSiteModel InterimSite(int id, bool removed) =>
        new()
        {
            SiteId = id,
            SiteNumber = id.ToString("000"),
            Country = "Germany",
            SiteName = $"Interim {id}",
            AddressLine1 = "Line 1",
            TownOrCity = "Town",
            ContactName = "n",
            ContactEmail = "e@example.com",
            ContactPhone = "1",
            RemovedAt = removed ? DateTime.UtcNow : null,
        };
}
