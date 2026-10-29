using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.DomainModel.Entities;
using EprRegisterEnrolBackend.DomainModel.Seeding;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Seeding;

public class SeedEntityMapperTests
{
    private static ReExAccreditationDto Dto() =>
        new()
        {
            OrganisationName = "Acme Recycling Ltd",
            CompaniesHouseNumber = "01234567",
            RegistrationReference = "R26EX1234567P",
            IsExporter = true,
            Nation = Nation.Wales,
            GlassRecyclingProcess = null,
        };

    private static OverseasSiteModel Site() =>
        new()
        {
            SiteId = 1,
            OrsId = "001",
            SiteName = "Site One",
            SiteAddress = "1 Rue Test, Lyon, France",
            AddressLine1 = "1 Rue Test",
            TownOrCity = "Lyon",
            Country = "France",
            Coordinates = "45.76, 4.83",
            IsEu = true,
            IsOecd = true,
            Code1 = "B3011",
            Code2 = " ",
            Code3 = "GH013",
        };

    [Fact]
    public void ApplyToOrganisation_ValuesPresent_CopiesThemAndFlagsEnrichment()
    {
        var target = new OrganisationEntity { ReExOrganisationId = "org" };

        SeedEntityMapper.ApplyTo(target, Dto());

        Assert.Equal("Acme Recycling Ltd", target.Name);
        Assert.Equal("01234567", target.CompaniesHouseNumber);
        Assert.Equal(SourceSystem.ReEx, target.Source.System);
        Assert.True(target.Source.EnrichmentPending);
    }

    [Fact]
    public void ApplyToOrganisation_ValuesMissing_KeepsWhatIsAlreadyHeld()
    {
        var target = new OrganisationEntity
        {
            ReExOrganisationId = "org",
            Name = "Held Name",
            CompaniesHouseNumber = "99999999",
        };
        var dto = Dto();
        dto.OrganisationName = null;
        dto.CompaniesHouseNumber = " ";

        SeedEntityMapper.ApplyTo(target, dto);

        Assert.Equal("Held Name", target.Name);
        Assert.Equal("99999999", target.CompaniesHouseNumber);
    }

    [Fact]
    public void ApplyToRegistration_Exporter_CopiesReExFields()
    {
        var target = new RegistrationEntity();

        SeedEntityMapper.ApplyTo(target, Dto(), "reg-1", MaterialType.Plastic);

        Assert.Equal("reg-1", target.ReExRegistrationId);
        Assert.Equal(MaterialType.Plastic, target.Material);
        Assert.Equal(Nation.Wales, target.Nation);
        Assert.Equal(WasteProcessingKind.Exporter, target.WasteProcessingType);
        Assert.Equal("R26EX1234567P", target.RegistrationNumber);
        Assert.Null(target.GlassRecyclingProcess);
        Assert.Equal(SourceSystem.ReEx, target.Source.System);
        Assert.True(target.Source.EnrichmentPending);
    }

    [Fact]
    public void ApplyToRegistration_Reprocessor_SetsReprocessorKind()
    {
        var dto = Dto();
        dto.IsExporter = false;
        var target = new RegistrationEntity();

        SeedEntityMapper.ApplyTo(target, dto, "reg-1", MaterialType.Plastic);

        Assert.Equal(WasteProcessingKind.Reprocessor, target.WasteProcessingType);
    }

    [Fact]
    public void ApplyToRegistration_ExistingEntity_KeepsStatusPreviousNumbersAndHeldNumber()
    {
        var target = new RegistrationEntity
        {
            RegistrationNumber = "R27EX0000001P",
            PreviousRegistrationNumbers = ["R26EX0000001P"],
            Status = RegistrationStatus.Approved,
        };
        var dto = Dto();
        dto.RegistrationReference = null;

        SeedEntityMapper.ApplyTo(target, dto, "reg-1", MaterialType.Plastic);

        Assert.Equal("R27EX0000001P", target.RegistrationNumber);
        Assert.Equal(["R26EX0000001P"], target.PreviousRegistrationNumbers);
        Assert.Equal(RegistrationStatus.Approved, target.Status);
    }

    [Fact]
    public void ApplyToOverseasSite_AnySite_CopiesReExFieldsAndListsNonEmptyCodesInOrder()
    {
        var target = new OverseasSiteEntity();

        SeedEntityMapper.ApplyTo(target, Site());

        Assert.Equal("001", target.OrsId);
        Assert.Equal("Site One", target.SiteName);
        Assert.Equal("1 Rue Test, Lyon, France", target.FormattedAddress);
        Assert.Equal("1 Rue Test", target.Address!.Line1);
        Assert.Null(target.Address.Line2);
        Assert.Equal("Lyon", target.Address.TownOrCity);
        Assert.Equal("France", target.Address.Country);
        Assert.Equal("France", target.Country);
        Assert.True(target.IsEu);
        Assert.True(target.IsOecd);
        Assert.Equal("45.76, 4.83", target.Coordinates);
        Assert.Equal(["B3011", "GH013"], target.BaselOecdCodes);
    }

    [Fact]
    public void ApplyToOverseasSite_NoAddressParts_LeavesAddressNull()
    {
        var site = Site();
        site.AddressLine1 = null;
        site.TownOrCity = null;
        site.Country = null;

        var target = new OverseasSiteEntity();
        SeedEntityMapper.ApplyTo(target, site);

        Assert.Null(target.Address);
    }

    [Fact]
    public void ApplyToOverseasSite_ExistingEntity_KeepsStatusOriginAndInterimSites()
    {
        var target = new OverseasSiteEntity
        {
            Status = OverseasSiteStatus.Discarded,
            Origin = SiteOrigin.Operator,
            InterimSites = [new InterimSiteEntry { SiteNumber = "001" }],
        };

        SeedEntityMapper.ApplyTo(target, Site());

        Assert.Equal(OverseasSiteStatus.Discarded, target.Status);
        Assert.Equal(SiteOrigin.Operator, target.Origin);
        Assert.Single(target.InterimSites);
    }

    [Fact]
    public void NewReExSite_AnySite_IsAnActiveReExSiteOnTheRegistration()
    {
        var registrationId = ObjectId.GenerateNewId();

        var site = SeedEntityMapper.NewReExSite(registrationId, Site());

        Assert.Equal(registrationId, site.RegistrationId);
        Assert.Equal(SiteOrigin.ReEx, site.Origin);
        Assert.Equal(OverseasSiteStatus.Active, site.Status);
        Assert.Equal("001", site.OrsId);
    }
}
