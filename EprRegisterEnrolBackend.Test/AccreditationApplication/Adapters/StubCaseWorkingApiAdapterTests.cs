using System.Net;
using System.Text.RegularExpressions;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.AccreditationChanges;
using EprRegisterEnrolBackend.DevSeed;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

/// <summary>
/// StubCaseWorkingApiAdapter is wired into Program.cs whenever CaseWorking__UseStub is true
/// (local/dev), giving realistic-looking case working responses with no real Case Working
/// Service to call. Nothing previously exercised it directly.
/// </summary>
public class StubCaseWorkingApiAdapterTests
{
    private static StubCaseWorkingApiAdapter BuildSut() =>
        new(EnabledNullLogger<StubCaseWorkingApiAdapter>.Instance);

    private static AccreditationApplicationModel CreateApplication(
        string? siteAddress = "123 High Street, London, SW1A 1AA",
        string organisationId = "12345",
        int? orgId = null,
        int year = 2026,
        MaterialType materialType = MaterialType.Plastic
    ) =>
        new()
        {
            OrganisationId = organisationId,
            OrgId = orgId,
            OrganisationName = "Acme Recycling Ltd",
            Year = year,
            RegistrationId = "reg-001",
            RegistrationReference = "R25ER1000230001PL",
            MaterialType = materialType,
            ApplicationStatus = ApplicationStatus.Started,
            SiteAddress = siteAddress,
            WasteProcessingType = "reprocessor",
        };

    [Fact]
    public async Task SubmitApplicationAsync_ReturnsGeneratedReferenceAndWorkItemId()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(),
            TestContext.Current.CancellationToken
        );

        result.CaseReference.Should().NotBeNullOrWhiteSpace();
        result.WorkItemId.Should().NotBeNull();
    }

    // RA-503: the Application ID is no longer used as a bank payment reference, so the previous
    // 18-character truncation was removed - a long OrganisationId (ReEx's internal ObjectId) must
    // no longer even reach the reference, let alone be truncated. See the OrgId tests below.
    [Fact]
    public async Task SubmitApplicationAsync_ReferenceIsNotTruncated()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(organisationId: "1234567890123456789", orgId: 500500),
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.CaseReference.Should().Be("AP26EA5005001AAPL");
    }

    // The E2E specs give each disposable application a made-up year in 3000-3999 on one
    // organisation, registration and material. Two of those years with the same last two digits
    // must not share a reference, or the second submit breaks the unique applicationReference
    // index. A real accreditation year keeps the two-digit format.
    [Fact]
    public async Task SubmitApplicationAsync_YearsOutsideThe2000s_DoNotShareAReference()
    {
        var sut = BuildSut();
        var ct = TestContext.Current.CancellationToken;

        var first = await sut.SubmitApplicationAsync(
            CreateApplication(orgId: 50003, year: 3039),
            ct
        );
        var second = await sut.SubmitApplicationAsync(
            CreateApplication(orgId: 50003, year: 3139),
            ct
        );
        var real = await sut.SubmitApplicationAsync(
            CreateApplication(orgId: 50003, year: 2039),
            ct
        );

        first.CaseReference.Should().NotBe(second.CaseReference);
        first.CaseReference.Should().StartWith("AP3039");
        real.CaseReference.Should().StartWith("AP39");
    }

    // RA-503: the reference must embed the operator/regulator-safe numeric OrgId, never
    // OrganisationId (ReEx's internal ObjectId) - mirrors the real ApplicationReferenceGenerator fix.
    [Fact]
    public async Task SubmitApplicationAsync_ReferenceEmbedsOrgIdNotOrganisationId()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(organisationId: "6a74a6a12b7c39b0cc15ca55", orgId: 500500),
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.CaseReference.Should().Contain("500500");
        result.CaseReference.Should().NotContain("6A74A6");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NullOrgId_OmitsOrgSegmentRatherThanFailing()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(orgId: null),
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.CaseReference.Should().Be("AP26EA1AAPL");
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
            CreateApplication(siteAddress: $"1 Test Street, Testville, {postcode}"),
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.CaseReference.Should().Contain(expectedAgencyCode);
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoSiteAddress_DefaultsToEnglandAgencyCode()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(siteAddress: null),
            TestContext.Current.CancellationToken
        );

        result.CaseReference.Should().Contain("EA");
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
            CreateApplication(materialType: (MaterialType)9),
            cancellationToken: TestContext.Current.CancellationToken
        );

        result
            .CaseReference.Should()
            .EndWith(
                "9",
                because: "MaterialPrefix takes the whole raw value verbatim when it's already <= 2 characters"
            );
    }

    [Fact]
    public async Task GetNotificationStatusAsync_ReturnsNullStatusAndDueDate()
    {
        var sut = BuildSut();

        var result = await sut.GetNotificationStatusAsync(
            CreateApplication(),
            TestContext.Current.CancellationToken
        );

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
            ["business-plan"],
            cancellationToken: TestContext.Current.CancellationToken
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
            "No longer required",
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Should().Be(WithdrawResult.Succeeded);
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

    // RA-492: the stub mirrors ManagementBe's in-year change reference (format: see "Application
    // reference" in ManagementBe's docs/work-items.md).
    private static AccreditationChangeRequest CreateChangeRequest(
        int year = 2026,
        string changeType = AccreditationChangeRequest.AddOverseasSiteChangeType
    )
    {
        var accreditation = DevExporterAccreditationSeedService.ExporterAccreditation(
            DevExporterOrg.InYearChange,
            2026
        );
        return new AccreditationChangeRequest
        {
            ChangeType = changeType,
            OrganisationId = accreditation.OrganisationId,
            ApplicationId = accreditation.ApplicationId!,
            MaterialType = accreditation.MaterialType,
            Year = year,
        };
    }

    private static Task<CaseWorkingSubmissionResult> SubmitChange(
        StubCaseWorkingApiAdapter sut,
        AccreditationChangeRequest request
    ) =>
        sut.SubmitChangeRequestAsync(
            request,
            DevExporterAccreditationSeedService.ExporterAccreditation(DevExporterOrg.InYearChange, 2026),
            TestContext.Current.CancellationToken
        );

    [Fact]
    public async Task SubmitChangeRequestAsync_ReturnsAnInYearChangeReferenceForTheRequestYear()
    {
        var result = await SubmitChange(BuildSut(), CreateChangeRequest(year: 2026));

        result.CaseReference.Should().Be("IYC-ORS-2026-00000");
        result.WorkItemId.Should().NotBeNull();
    }

    [Fact]
    public async Task SubmitChangeRequestAsync_NumbersEachChangeInTurnFromZero_ZeroPaddedToFiveDigits()
    {
        var sut = BuildSut();

        var references = new List<string>();
        for (var i = 0; i < 12; i++)
            references.Add((await SubmitChange(sut, CreateChangeRequest())).CaseReference);

        references[0].Should().Be("IYC-ORS-2026-00000");
        references[1].Should().Be("IYC-ORS-2026-00001");
        references[11].Should().Be("IYC-ORS-2026-00011");
        references.Should().OnlyContain(r => Regex.IsMatch(
            r,
            "^IYC-ORS-2026-[0-9]{5}$"
        ));
    }

    [Fact]
    public async Task SubmitChangeRequestAsync_NumbersEachYearSeparately()
    {
        var sut = BuildSut();

        var first2026 = await SubmitChange(sut, CreateChangeRequest(year: 2026));
        var first2027 = await SubmitChange(sut, CreateChangeRequest(year: 2027));
        var second2026 = await SubmitChange(sut, CreateChangeRequest(year: 2026));

        first2026.CaseReference.Should().Be("IYC-ORS-2026-00000");
        first2027.CaseReference.Should().Be("IYC-ORS-2027-00000");
        second2026.CaseReference.Should().Be("IYC-ORS-2026-00001");
    }

    // The counter keeps increasing; the reference shows it modulo 100000.
    [Theory]
    [InlineData(99_998L, "IYC-ORS-2026-99998")]
    [InlineData(99_999L, "IYC-ORS-2026-99999")]
    [InlineData(100_000L, "IYC-ORS-2026-00000")]
    [InlineData(100_001L, "IYC-ORS-2026-00001")]
    [InlineData(200_000L, "IYC-ORS-2026-00000")]
    public void FormatChangeReference_WrapsTheSequenceToFiveDigits(long sequence, string expected) =>
        StubCaseWorkingApiAdapter.FormatChangeReference("ORS", 2026, sequence).Should().Be(expected);

    [Fact]
    public async Task SubmitChangeRequestAsync_ConcurrentSubmissions_EachGetADifferentNumber()
    {
        var sut = BuildSut();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 200).Select(_ => Task.Run(() => SubmitChange(sut, CreateChangeRequest())))
        );

        results
            .Select(r => r.CaseReference)
            .Should()
            .BeEquivalentTo(Enumerable.Range(0, 200).Select(n => $"IYC-ORS-2026-{n:D5}"));
    }

    // ManagementBe refuses an accreditation year that is not four digits with a 400.
    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(10000)]
    public async Task SubmitChangeRequestAsync_YearNotFourDigits_IsRefusedAsManagementBeRefusesIt(int year)
    {
        var act = () => SubmitChange(BuildSut(), CreateChangeRequest(year: year));

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(1000, "IYC-ORS-1000-00000")]
    [InlineData(9999, "IYC-ORS-9999-00000")]
    public async Task SubmitChangeRequestAsync_FourDigitYearBounds_AreAccepted(int year, string expected)
    {
        var result = await SubmitChange(BuildSut(), CreateChangeRequest(year: year));

        result.CaseReference.Should().Be(expected);
    }

    [Fact]
    public async Task SubmitChangeRequestAsync_UnknownChangeType_Throws()
    {
        var act = () => SubmitChange(BuildSut(), CreateChangeRequest(changeType: "remove-overseas-site"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*remove-overseas-site*");
    }

    // The change-request numbering must not leak into application references.
    [Fact]
    public async Task SubmitApplicationAsync_AfterAChangeRequest_StillReturnsTheApFormat()
    {
        var sut = BuildSut();
        await SubmitChange(sut, CreateChangeRequest());

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(orgId: 500500),
            TestContext.Current.CancellationToken
        );

        result.CaseReference.Should().Be("AP26EA5005001AAPL");
    }
}
