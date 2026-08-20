using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

/// <summary>
/// StubCaseWorkingApiAdapter is wired into Program.cs whenever CaseWorking__UseStub is true
/// (local/dev), giving realistic-looking case working responses with no real Case Working
/// Service to call. Nothing previously exercised it directly.
/// </summary>
public class StubCaseWorkingApiAdapterTests
{
    private static StubCaseWorkingApiAdapter BuildSut() =>
        new(NullLogger<StubCaseWorkingApiAdapter>.Instance);

    private static AccreditationApplicationModel CreateApplication(
        string? siteAddress = "123 High Street, London, SW1A 1AA",
        string organisationId = "12345",
        int year = 2026,
        MaterialType materialType = MaterialType.Plastic
    ) =>
        new()
        {
            OrganisationId = organisationId,
            OrganisationName = "Acme Recycling Ltd",
            Year = year,
            RegistrationId = "reg-001",
            RegistrationReference = "EPR-100023",
            MaterialType = materialType,
            ApplicationStatus = ApplicationStatus.Started,
            SiteAddress = siteAddress,
            WasteProcessingType = "reprocessor",
        };

    [Fact]
    public async Task SubmitApplicationAsync_ReturnsGeneratedReferenceAndWorkItemId()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(CreateApplication());

        result.ApplicationReference.Should().NotBeNullOrWhiteSpace();
        result.WorkItemId.Should().NotBeNull();
    }

    [Fact]
    public async Task SubmitApplicationAsync_ReferenceIsNoLongerThan18Characters()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(organisationId: "1234567890123456789")
        );

        result.ApplicationReference.Length.Should().BeLessThanOrEqualTo(18);
    }

    [Theory]
    [InlineData("BT1 1AA", "NI")]
    [InlineData("EH1 1AA", "SE")]
    [InlineData("CF10 1AA", "NR")]
    [InlineData("SW1A 1AA", "EA")]
    public async Task SubmitApplicationAsync_ResolvesAgencyCodeFromPostcode(
        string postcode,
        string expectedAgencyCode
    )
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(siteAddress: $"1 Test Street, Testville, {postcode}")
        );

        result.ApplicationReference.Should().Contain(expectedAgencyCode);
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoSiteAddress_DefaultsToEnglandAgencyCode()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(CreateApplication(siteAddress: null));

        result.ApplicationReference.Should().Contain("EA");
    }

    // Covers MaterialPrefix's `material.Length <= 2` true branch. Every defined MaterialType
    // enum value (Steel, Wood, Aluminium, Fibre, Glass, Paper, Plastic) is 4+ characters long, so
    // that branch is unreachable via any real MaterialType — an undefined enum value is the only
    // way to exercise it, and Enum.ToString() on an undefined value returns the raw numeric
    // string (e.g. "9"), which is legitimately <= 2 characters.
    [Fact]
    public async Task SubmitApplicationAsync_UndefinedMaterialTypeValue_UsesRawNumericPrefix()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(materialType: (MaterialType)9)
        );

        result.ApplicationReference.Should()
            .EndWith(
                "9",
                because: "MaterialPrefix takes the whole raw value verbatim when it's already <= 2 characters"
            );
    }

    [Fact]
    public async Task GetNotificationStatusAsync_ReturnsNullStatusAndDueDate()
    {
        var sut = BuildSut();

        var result = await sut.GetNotificationStatusAsync(CreateApplication());

        result.NotificationStatus.Should().BeNull();
        result.SlaDueDate.Should().BeNull();
    }

    [Fact]
    public async Task ResumeFromQueryAsync_ReturnsSuccess()
    {
        var sut = BuildSut();

        var result = await sut.ResumeFromQueryAsync(
            CreateApplication(),
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Operations Manager",
            },
            ["business-plan"]
        );

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task WithdrawApplicationAsync_ReturnsSuccess()
    {
        var sut = BuildSut();

        var result = await sut.WithdrawApplicationAsync(
            CreateApplication(),
            new QuerySubmitterContactDetails
            {
                FullName = "Jane Smith",
                Email = "jane@example.com",
                Role = "Operations Manager",
            },
            "No longer required"
        );

        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_CompletesWithoutThrowing()
    {
        var sut = BuildSut();

        var act = () =>
            sut.NotifySiteAddedAsync(CreateApplication(), "interim", "ORS123", "SITE001", true);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task NotifySiteAddedAsync_NewOverseasSite_CompletesWithoutThrowing()
    {
        var sut = BuildSut();

        var act = () =>
            sut.NotifySiteAddedAsync(CreateApplication(), "overseas", "ORS456", null, false);

        await act.Should().NotThrowAsync();
    }
}
