using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Organisation.Services;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.Organisation.Services;

/// <summary>
/// Coverage for FakeOrganisationPersistence's CRUD/search surface. The seeded fixture data in
/// its constructor is exercised indirectly by StubReExApiAdapter's dev-mode tests elsewhere;
/// this file targets the in-memory store operations (create/get/search/update/delete/upsert)
/// that nothing previously exercised directly.
/// </summary>
public class FakeOrganisationPersistenceTests
{
    private static OrganisationModel NewOrg(int orgId, string name = "New Test Org") =>
        new()
        {
            OrgId = orgId,
            SchemaVersion = 1,
            Version = 1,
            BusinessType = "individual",
            WasteProcessingTypes = ["reprocessor"],
            ReprocessingNations = ["england"],
            CompanyDetails = new CompanyDetailsModel { Name = name },
            ContactDetails = new ContactDetailsModel
            {
                FullName = "Test Contact",
                Email = "test.contact@example.test",
            },
            Users = [],
        };

    [Fact]
    public async Task CreateAsync_NewOrgId_AddsOrganisationAndReturnsTrue()
    {
        var sut = new FakeOrganisationPersistence();

        var created = await sut.CreateAsync(NewOrg(999001));

        created.Should().BeTrue();
        var stored = await sut.GetByOrgIdAsync(999001);
        stored.Should().NotBeNull();
        stored!.CompanyDetails!.Name.Should().Be("New Test Org");
    }

    [Fact]
    public async Task CreateAsync_DuplicateOrgId_ReturnsFalseAndDoesNotOverwrite()
    {
        var sut = new FakeOrganisationPersistence();
        await sut.CreateAsync(NewOrg(999002, "Original Name"));

        var created = await sut.CreateAsync(NewOrg(999002, "Duplicate Name"));

        created.Should().BeFalse();
        var stored = await sut.GetByOrgIdAsync(999002);
        stored!.CompanyDetails!.Name.Should().Be("Original Name");
    }

    [Fact]
    public async Task GetByOrgIdAsync_SeededOrg_ReturnsIt()
    {
        var sut = new FakeOrganisationPersistence();

        var result = await sut.GetByOrgIdAsync(1);

        result.Should().NotBeNull();
        result!.CompanyDetails!.Name.Should().Be("Operator Export Company");
    }

    [Fact]
    public async Task GetByOrgIdAsync_UnknownOrgId_ReturnsNull()
    {
        var sut = new FakeOrganisationPersistence();

        var result = await sut.GetByOrgIdAsync(int.MaxValue);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllSeededOrganisationsAsSummaries()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.GetAllAsync()).ToList();

        result.Should().NotBeEmpty();
        result.Should().Contain(o => o.OrgId == 1);
        result.Should().Contain(o => o.OrgId == 50001);
    }

    [Fact]
    public async Task GetAllAsync_ReflectsSubsequentCreate()
    {
        var sut = new FakeOrganisationPersistence();
        var before = (await sut.GetAllAsync()).Count();

        await sut.CreateAsync(NewOrg(999003));

        var after = (await sut.GetAllAsync()).Count();
        after.Should().Be(before + 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SearchByValueAsync_NullOrWhitespaceTerm_ReturnsAllOrganisations(
        string? term
    )
    {
        var sut = new FakeOrganisationPersistence();

        var all = (await sut.GetAllAsync()).Count();
        var result = (await sut.SearchByValueAsync(term!)).ToList();

        result.Should().HaveCount(all);
    }

    [Fact]
    public async Task SearchByValueAsync_MatchesCompanyName_CaseInsensitive()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.SearchByValueAsync("operator export")).ToList();

        result.Should().ContainSingle(o => o.OrgId == 1);
    }

    [Fact]
    public async Task SearchByValueAsync_MatchesTradingName()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.SearchByValueAsync("Op Export Co")).ToList();

        result.Should().Contain(o => o.OrgId == 1);
    }

    [Fact]
    public async Task SearchByValueAsync_MatchesRegistrationNumber()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.SearchByValueAsync("99999999")).ToList();

        result.Should().ContainSingle(o => o.OrgId == 2);
    }

    [Fact]
    public async Task SearchByValueAsync_MatchesContactFullName()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.SearchByValueAsync("Jane Example")).ToList();

        result.Should().ContainSingle(o => o.OrgId == 2);
    }

    [Fact]
    public async Task SearchByValueAsync_MatchesContactEmail()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.SearchByValueAsync("aysha@thirdcompany.co.uk")).ToList();

        result.Should().ContainSingle(o => o.OrgId == 3);
    }

    [Fact]
    public async Task SearchByValueAsync_TrimsSearchTerm()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.SearchByValueAsync("  Jane Example  ")).ToList();

        result.Should().ContainSingle(o => o.OrgId == 2);
    }

    [Fact]
    public async Task SearchByValueAsync_NoMatch_ReturnsEmpty()
    {
        var sut = new FakeOrganisationPersistence();

        var result = (await sut.SearchByValueAsync("no-such-organisation-xyz")).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task UpdateAsync_ExistingOrg_UpdatesAndReturnsTrue()
    {
        var sut = new FakeOrganisationPersistence();
        var org = await sut.GetByOrgIdAsync(1);
        org!.CompanyDetails!.Name = "Updated Name";

        var updated = await sut.UpdateAsync(org);

        updated.Should().BeTrue();
        var stored = await sut.GetByOrgIdAsync(1);
        stored!.CompanyDetails!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateAsync_UnknownOrg_ReturnsFalse()
    {
        var sut = new FakeOrganisationPersistence();

        var updated = await sut.UpdateAsync(NewOrg(int.MaxValue - 1));

        updated.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteAsync_ExistingOrg_RemovesItAndReturnsTrue()
    {
        var sut = new FakeOrganisationPersistence();
        await sut.CreateAsync(NewOrg(999004));

        var deleted = await sut.DeleteAsync(999004);

        deleted.Should().BeTrue();
        (await sut.GetByOrgIdAsync(999004)).Should().BeNull();
    }

    [Fact]
    public async Task DeleteAsync_UnknownOrg_ReturnsFalse()
    {
        var sut = new FakeOrganisationPersistence();

        var deleted = await sut.DeleteAsync(int.MaxValue - 2);

        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertAsync_ExistingOrg_ReplacesIt()
    {
        var sut = new FakeOrganisationPersistence();
        await sut.CreateAsync(NewOrg(999005, "Before Upsert"));

        var result = await sut.UpsertAsync(NewOrg(999005, "After Upsert"));

        result.Should().BeTrue();
        var stored = await sut.GetByOrgIdAsync(999005);
        stored!.CompanyDetails!.Name.Should().Be("After Upsert");
    }

    [Fact]
    public async Task UpsertAsync_NewOrg_InsertsIt()
    {
        var sut = new FakeOrganisationPersistence();

        var result = await sut.UpsertAsync(NewOrg(999006, "Inserted via Upsert"));

        result.Should().BeTrue();
        var stored = await sut.GetByOrgIdAsync(999006);
        stored.Should().NotBeNull();
        stored!.CompanyDetails!.Name.Should().Be("Inserted via Upsert");
    }

    [Fact]
    public async Task SearchByValueAsync_OrgWithNullCompanyAndContactDetails_IsExcludedAndDoesNotThrow()
    {
        // Exercises the outer o.CompanyDetails?.* / o.ContactDetails?.* null-conditional
        // branches — every seeded fixture and NewOrg() always sets both, so nothing else
        // reaches the case where the whole CompanyDetails/ContactDetails object is null
        // (as opposed to just one of its string properties being null).
        var sut = new FakeOrganisationPersistence();
        await sut.CreateAsync(
            new OrganisationModel
            {
                OrgId = 999007,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = "individual",
                WasteProcessingTypes = ["reprocessor"],
                ReprocessingNations = ["england"],
                CompanyDetails = null,
                ContactDetails = null,
                Users = [],
            }
        );

        var act = () => sut.SearchByValueAsync("some-term-that-matches-nothing");
        await act.Should().NotThrowAsync();

        var result = (await act()).ToList();
        result.Should().NotContain(o => o.OrgId == 999007);
    }

    [Fact]
    public async Task SearchByValueAsync_MatchesEmail_WhenCompanyDetailsIsNull()
    {
        // Company-detail conditions (Name/TradingName/RegistrationNumber) must all
        // short-circuit to false via the null-conditional when CompanyDetails itself is
        // null, falling through to the ContactDetails.Email match later in the OR chain.
        var sut = new FakeOrganisationPersistence();
        await sut.CreateAsync(
            new OrganisationModel
            {
                OrgId = 999008,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = "individual",
                WasteProcessingTypes = ["reprocessor"],
                ReprocessingNations = ["england"],
                CompanyDetails = null,
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "No Company Details",
                    Email = "no-company-details@example.test",
                },
                Users = [],
            }
        );

        var result = (await sut.SearchByValueAsync("no-company-details@example.test")).ToList();

        result.Should().ContainSingle(o => o.OrgId == 999008);
    }
}
