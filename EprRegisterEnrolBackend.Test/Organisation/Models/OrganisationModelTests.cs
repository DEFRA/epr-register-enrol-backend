using EprRegisterEnrolBackend.Organisation.Models;
using FluentAssertions;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.Organisation.Models;

/// <summary>
/// These are plain data-transfer models with no behaviour of their own, but nothing previously
/// constructed most of them with every property populated — this exercises every
/// getter/setter so the shape stays as documented and future property renames/removals show up
/// as a compile break here rather than only in production JSON/BSON round-tripping.
/// </summary>
public class OrganisationModelTests
{
    [Fact]
    public void OrganisationModel_AllPropertiesRoundTrip()
    {
        var model = new OrganisationModel
        {
            OrgId = 42,
            SchemaVersion = 1,
            Version = 2,
            WasteProcessingTypes = ["reprocessor"],
            ReprocessingNations = ["england"],
            BusinessType = "individual",
            CompanyDetails = new CompanyDetailsModel(),
            Partnership = new PartnershipModel(),
            ContactDetails = new ContactDetailsModel(),
            SubmitterContactDetails = new ContactDetailsModel(),
            SubmittedToRegulator = "ea",
            Users = [new PersonModel()],
            Registrations =
            [
                new RegistrationModel
                {
                    Status = "created",
                    Material = "plastic",
                    WasteProcessingType = "reprocessor",
                },
            ],
            Accreditations =
            [
                new AccreditationModel
                {
                    Status = "created",
                    Material = "plastic",
                    WasteProcessingType = "reprocessor",
                },
            ],
            FormSubmissionRawDataId = "raw-data-id",
        };

        model.OrgId.Should().Be(42);
        model.SchemaVersion.Should().Be(1);
        model.Version.Should().Be(2);
        model.WasteProcessingTypes.Should().ContainSingle();
        model.ReprocessingNations.Should().ContainSingle();
        model.BusinessType.Should().Be("individual");
        model.CompanyDetails.Should().NotBeNull();
        model.Partnership.Should().NotBeNull();
        model.ContactDetails.Should().NotBeNull();
        model.SubmitterContactDetails.Should().NotBeNull();
        model.SubmittedToRegulator.Should().Be("ea");
        model.Users.Should().ContainSingle();
        model.Registrations.Should().ContainSingle();
        model.Accreditations.Should().ContainSingle();
        model.FormSubmissionRawDataId.Should().Be("raw-data-id");
    }

    [Fact]
    public void OrganisationSummaryModel_AllPropertiesRoundTrip()
    {
        var model = new OrganisationSummaryModel
        {
            OrgId = 7,
            WasteProcessingTypes = ["exporter"],
            ReprocessingNations = ["wales"],
            BusinessType = "partnership",
            CompanyDetails = new CompanyDetailsModel(),
            Partnership = new PartnershipModel(),
            ContactDetails = new ContactDetailsModel(),
            SubmittedToRegulator = "sepa",
        };

        model.OrgId.Should().Be(7);
        model.WasteProcessingTypes.Should().ContainSingle();
        model.ReprocessingNations.Should().ContainSingle();
        model.BusinessType.Should().Be("partnership");
        model.CompanyDetails.Should().NotBeNull();
        model.Partnership.Should().NotBeNull();
        model.ContactDetails.Should().NotBeNull();
        model.SubmittedToRegulator.Should().Be("sepa");
    }

    [Fact]
    public void CompanyDetailsModel_AllPropertiesRoundTrip()
    {
        var model = new CompanyDetailsModel
        {
            Name = "Acme Ltd",
            TradingName = "Acme",
            RegistrationNumber = "12345678",
            CompaniesHouseNumber = "87654321",
            RegisteredAddress = new RegisteredAddressModel
            {
                Line1 = "1 Test St",
                Town = "Testville",
                Postcode = "AB1 2CD",
            },
        };

        model.Name.Should().Be("Acme Ltd");
        model.TradingName.Should().Be("Acme");
        model.RegistrationNumber.Should().Be("12345678");
        model.CompaniesHouseNumber.Should().Be("87654321");
        model.RegisteredAddress.Should().NotBeNull();
    }

    [Fact]
    public void RegisteredAddressModel_AllPropertiesRoundTrip()
    {
        var model = new RegisteredAddressModel
        {
            Line1 = "1 Test St",
            Line2 = "Suite 2",
            Town = "Testville",
            County = "Testshire",
            Country = "England",
            Postcode = "AB1 2CD",
            Region = "North West",
        };

        model.Line1.Should().Be("1 Test St");
        model.Line2.Should().Be("Suite 2");
        model.Town.Should().Be("Testville");
        model.County.Should().Be("Testshire");
        model.Country.Should().Be("England");
        model.Postcode.Should().Be("AB1 2CD");
        model.Region.Should().Be("North West");
    }

    [Fact]
    public void PartnershipModel_AllPropertiesRoundTrip()
    {
        var model = new PartnershipModel
        {
            Type = "limited",
            Partners = [new PartnerModel { Name = "Partner One", Type = "individual" }],
        };

        model.Type.Should().Be("limited");
        model.Partners.Should().ContainSingle();
    }

    [Fact]
    public void PartnerModel_AllPropertiesRoundTrip()
    {
        var model = new PartnerModel { Name = "Partner One", Type = "individual" };

        model.Name.Should().Be("Partner One");
        model.Type.Should().Be("individual");
    }

    [Fact]
    public void ContactDetailsModel_AllPropertiesRoundTrip()
    {
        var model = new ContactDetailsModel
        {
            FullName = "Jane Doe",
            Email = "jane@example.test",
            Phone = "01234567890",
            Role = "Director",
            Title = "Mrs",
        };

        model.FullName.Should().Be("Jane Doe");
        model.Email.Should().Be("jane@example.test");
        model.Phone.Should().Be("01234567890");
        model.Role.Should().Be("Director");
        model.Title.Should().Be("Mrs");
    }

    [Fact]
    public void PersonModel_AllPropertiesRoundTrip()
    {
        var model = new PersonModel
        {
            FullName = "John Doe",
            Email = "john@example.test",
            Phone = "01234567890",
            Title = "Mr",
            Role = "Director",
        };

        model.FullName.Should().Be("John Doe");
        model.Email.Should().Be("john@example.test");
        model.Phone.Should().Be("01234567890");
        model.Title.Should().Be("Mr");
        model.Role.Should().Be("Director");
    }

    [Fact]
    public void RegistrationModel_AllPropertiesRoundTrip()
    {
        var id = ObjectId.GenerateNewId();
        var accreditationId = ObjectId.GenerateNewId();
        var model = new RegistrationModel
        {
            Id = id,
            SiteId = "SITE1",
            FormSubmissionTime = "2026-01-01T00:00:00Z",
            Status = "created",
            Material = "plastic",
            GlassRecyclingProcess = "glass_re_melt",
            WasteProcessingType = "reprocessor",
            AccreditationId = accreditationId,
            GridReference = "TQ 132 546",
            WasteRegistrationNumber = "WRN123",
            Suppliers = "Supplier A",
            PlantEquipmentDetails = "Shredder",
            FormSubmissionRawDataId = "raw-id",
            SiteAddress = new SiteAddressModel { Line1 = "1 Site Rd" },
            NoticeAddress = new NoticeAddressModel { Line1 = "1 Notice Rd" },
            RecyclingProcess = ["mechanical"],
            ExportPorts = ["Southampton"],
            WasteManagementPermits = [new WasteManagementPermitModel { PermitNumber = "WML1" }],
            ApprovedPersons = [new PersonModel { FullName = "Approver" }],
            YearlyMetrics = new YearlyMetricsModel { Year = "2026" },
            ContactDetails = new ContactDetailsModel { FullName = "Reg Contact" },
            SubmitterContactDetails = new ContactDetailsModel { FullName = "Submitter" },
            SamplingInspectionPlan = ["plan-1"],
            OverseasSites = ["900001"],
        };

        model.Id.Should().Be(id);
        model.SiteId.Should().Be("SITE1");
        model.FormSubmissionTime.Should().Be("2026-01-01T00:00:00Z");
        model.Status.Should().Be("created");
        model.Material.Should().Be("plastic");
        model.GlassRecyclingProcess.Should().Be("glass_re_melt");
        model.WasteProcessingType.Should().Be("reprocessor");
        model.AccreditationId.Should().Be(accreditationId);
        model.GridReference.Should().Be("TQ 132 546");
        model.WasteRegistrationNumber.Should().Be("WRN123");
        model.Suppliers.Should().Be("Supplier A");
        model.PlantEquipmentDetails.Should().Be("Shredder");
        model.FormSubmissionRawDataId.Should().Be("raw-id");
        model.SiteAddress.Should().NotBeNull();
        model.NoticeAddress.Should().NotBeNull();
        model.RecyclingProcess.Should().ContainSingle();
        model.ExportPorts.Should().ContainSingle();
        model.WasteManagementPermits.Should().ContainSingle();
        model.ApprovedPersons.Should().ContainSingle();
        model.YearlyMetrics.Should().NotBeNull();
        model.ContactDetails.Should().NotBeNull();
        model.SubmitterContactDetails.Should().NotBeNull();
        model.SamplingInspectionPlan.Should().ContainSingle();
        model.OverseasSites.Should().ContainSingle();
    }

    [Fact]
    public void SiteAddressModel_AllPropertiesRoundTrip()
    {
        var model = new SiteAddressModel
        {
            Line1 = "1 Site Rd",
            Line2 = "Unit 2",
            Town = "Siteville",
            County = "Siteshire",
            Country = "England",
            Postcode = "SI1 2TE",
        };

        model.Line1.Should().Be("1 Site Rd");
        model.Line2.Should().Be("Unit 2");
        model.Town.Should().Be("Siteville");
        model.County.Should().Be("Siteshire");
        model.Country.Should().Be("England");
        model.Postcode.Should().Be("SI1 2TE");
    }

    [Fact]
    public void NoticeAddressModel_AllPropertiesRoundTrip()
    {
        var model = new NoticeAddressModel
        {
            Line1 = "1 Notice Rd",
            Line2 = "Unit 3",
            Town = "Noticeville",
            County = "Noticeshire",
            Country = "England",
            Postcode = "NO1 2TI",
        };

        model.Line1.Should().Be("1 Notice Rd");
        model.Line2.Should().Be("Unit 3");
        model.Town.Should().Be("Noticeville");
        model.County.Should().Be("Noticeshire");
        model.Country.Should().Be("England");
        model.Postcode.Should().Be("NO1 2TI");
    }

    [Fact]
    public void WasteManagementPermitModel_AllPropertiesRoundTrip()
    {
        var model = new WasteManagementPermitModel
        {
            Type = "environmental_permit",
            PermitNumber = "WML123",
            AuthorisedWeight = "1000",
            PermitWindow = "2026-2027",
            Exemptions = [new ExemptionModel { Reference = "EX1", ExemptionCode = "U1" }],
        };

        model.Type.Should().Be("environmental_permit");
        model.PermitNumber.Should().Be("WML123");
        model.AuthorisedWeight.Should().Be("1000");
        model.PermitWindow.Should().Be("2026-2027");
        model.Exemptions.Should().ContainSingle();
    }

    [Fact]
    public void ExemptionModel_AllPropertiesRoundTrip()
    {
        var model = new ExemptionModel { Reference = "EX1", ExemptionCode = "U1" };

        model.Reference.Should().Be("EX1");
        model.ExemptionCode.Should().Be("U1");
    }

    [Fact]
    public void YearlyMetricsModel_AllPropertiesRoundTrip()
    {
        var model = new YearlyMetricsModel
        {
            Year = "2026",
            Metric = "tonnage",
            Input = new MetricsInputModel { Type = "input" },
            Output = new MetricsOutputModel { Type = "output" },
        };

        model.Year.Should().Be("2026");
        model.Metric.Should().Be("tonnage");
        model.Input.Should().NotBeNull();
        model.Output.Should().NotBeNull();
    }

    [Fact]
    public void MetricsInputModel_AllPropertiesRoundTrip()
    {
        var model = new MetricsInputModel
        {
            Type = "input",
            UkPackagingWaste = 100,
            NonUkPackagingWaste = 50,
            NonPackagingWaste = 25,
        };

        model.Type.Should().Be("input");
        model.UkPackagingWaste.Should().Be(100);
        model.NonUkPackagingWaste.Should().Be(50);
        model.NonPackagingWaste.Should().Be(25);
    }

    [Fact]
    public void MetricsOutputModel_AllPropertiesRoundTrip()
    {
        var model = new MetricsOutputModel
        {
            Type = "output",
            SentToAnotherSite = 10,
            Contaminants = 5,
            ProcessLoss = 2,
        };

        model.Type.Should().Be("output");
        model.SentToAnotherSite.Should().Be(10);
        model.Contaminants.Should().Be(5);
        model.ProcessLoss.Should().Be(2);
    }

    [Fact]
    public void AccreditationModel_AllPropertiesRoundTrip()
    {
        var id = ObjectId.GenerateNewId();
        var model = new AccreditationModel
        {
            Id = id,
            FormSubmissionTime = "2026-01-01T00:00:00Z",
            Status = "created",
            Material = "plastic",
            GlassRecyclingProcess = "glass_other",
            WasteProcessingType = "reprocessor",
            FormSubmissionRawDataId = "raw-id",
            SiteAddress = new SiteAddressModel { Line1 = "1 Site Rd" },
            RecyclingProcess = ["mechanical"],
            PrnIssuance = new PrnIssuanceModel { PlannedIssuance = "quarterly" },
            BusinessPlan = [new BusinessPlanItemModel { Description = "desc" }],
            ContactDetails = new ContactDetailsModel { FullName = "Contact" },
            SubmitterContactDetails = new ContactDetailsModel { FullName = "Submitter" },
            SamplingInspectionPlan = ["plan-1"],
            OverseasSites = ["900001"],
        };

        model.Id.Should().Be(id);
        model.FormSubmissionTime.Should().Be("2026-01-01T00:00:00Z");
        model.Status.Should().Be("created");
        model.Material.Should().Be("plastic");
        model.GlassRecyclingProcess.Should().Be("glass_other");
        model.WasteProcessingType.Should().Be("reprocessor");
        model.FormSubmissionRawDataId.Should().Be("raw-id");
        model.SiteAddress.Should().NotBeNull();
        model.RecyclingProcess.Should().ContainSingle();
        model.PrnIssuance.Should().NotBeNull();
        model.BusinessPlan.Should().ContainSingle();
        model.ContactDetails.Should().NotBeNull();
        model.SubmitterContactDetails.Should().NotBeNull();
        model.SamplingInspectionPlan.Should().ContainSingle();
        model.OverseasSites.Should().ContainSingle();
    }

    [Fact]
    public void PrnIssuanceModel_AllPropertiesRoundTrip()
    {
        var model = new PrnIssuanceModel
        {
            PlannedIssuance = "quarterly",
            Signatories = [new PersonModel { FullName = "Signatory" }],
            PrnIncomeBusinessPlan = [new PrnIncomeBusinessPlanItemModel { Description = "desc" }],
        };

        model.PlannedIssuance.Should().Be("quarterly");
        model.Signatories.Should().ContainSingle();
        model.PrnIncomeBusinessPlan.Should().ContainSingle();
    }

    [Fact]
    public void PrnIncomeBusinessPlanItemModel_AllPropertiesRoundTrip()
    {
        var model = new PrnIncomeBusinessPlanItemModel
        {
            Description = "desc",
            DetailedDescription = "detailed desc",
            PercentSpent = 10,
            PercentIncomeSpent = 20,
            UsageDescription = "usage",
            DetailedExplanation = "explanation",
        };

        model.Description.Should().Be("desc");
        model.DetailedDescription.Should().Be("detailed desc");
        model.PercentSpent.Should().Be(10);
        model.PercentIncomeSpent.Should().Be(20);
        model.UsageDescription.Should().Be("usage");
        model.DetailedExplanation.Should().Be("explanation");
    }

    [Fact]
    public void BusinessPlanItemModel_AllPropertiesRoundTrip()
    {
        var model = new BusinessPlanItemModel
        {
            Description = "desc",
            DetailedDescription = "detailed desc",
            PercentSpent = 15,
        };

        model.Description.Should().Be("desc");
        model.DetailedDescription.Should().Be("detailed desc");
        model.PercentSpent.Should().Be(15);
    }
}
