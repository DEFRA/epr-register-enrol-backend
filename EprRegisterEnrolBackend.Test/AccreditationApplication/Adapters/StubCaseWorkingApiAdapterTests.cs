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

        result.Reference.Should().NotBeNullOrWhiteSpace();
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

        result.Reference.Should().Be("AP26EA5005001AAPL");
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

        first.Reference.Should().NotBe(second.Reference);
        first.Reference.Should().StartWith("AP3039");
        real.Reference.Should().StartWith("AP39");
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

        result.Reference.Should().Contain("500500");
        result.Reference.Should().NotContain("6A74A6");
    }

    [Fact]
    public async Task SubmitApplicationAsync_NullOrgId_OmitsOrgSegmentRatherThanFailing()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(orgId: null),
            cancellationToken: TestContext.Current.CancellationToken
        );

        result.Reference.Should().Be("AP26EA1AAPL");
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

        result.Reference.Should().Contain(expectedAgencyCode);
    }

    [Fact]
    public async Task SubmitApplicationAsync_NoSiteAddress_DefaultsToEnglandAgencyCode()
    {
        var sut = BuildSut();

        var result = await sut.SubmitApplicationAsync(
            CreateApplication(siteAddress: null),
            TestContext.Current.CancellationToken
        );

        result.Reference.Should().Contain("EA");
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
            .Reference.Should()
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

    // RA-492: ManagementBe gives a change request an in-year change reference,
    // IYC-{code}-{yyyy}-{seq:D5} (add-overseas-site -> ORS), numbered per code per year.
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    [Fact]
    public async Task SubmitChangeRequestAsync_ReturnsAnInYearChangeReferenceForTheRequestYear()
    {
        var result = await SubmitChange(BuildSut(), CreateChangeRequest(year: 2026));

        result.Reference.Should().Be("IYC-ORS-2026-00001");
        result.WorkItemId.Should().NotBeNull();
    }

    [Fact]
    public async Task SubmitChangeRequestAsync_NumbersEachChangeInTurn_ZeroPaddedToFiveDigits()
    {
        var sut = BuildSut();

        var references = new List<string>();
        for (var i = 0; i < 12; i++)
            references.Add((await SubmitChange(sut, CreateChangeRequest())).Reference);

        references[0].Should().Be("IYC-ORS-2026-00001");
        references[1].Should().Be("IYC-ORS-2026-00002");
        references[11].Should().Be("IYC-ORS-2026-00012");
        references.Should().OnlyContain(r => System.Text.RegularExpressions.Regex.IsMatch(
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

        first2026.Reference.Should().Be("IYC-ORS-2026-00001");
        first2027.Reference.Should().Be("IYC-ORS-2027-00001");
        second2026.Reference.Should().Be("IYC-ORS-2026-00002");
    }

    // ManagementBe falls back to the current UTC year when accreditationYear is not a four-digit
    // year; the stub does the same.
    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    [InlineData(10000)]
    public async Task SubmitChangeRequestAsync_YearNotFourDigits_UsesTheCurrentUtcYear(int year)
    {
        var sut = new StubCaseWorkingApiAdapter(
            EnabledNullLogger<StubCaseWorkingApiAdapter>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2031, 12, 31, 23, 0, 0, TimeSpan.FromHours(-5)))
        );

        var result = await SubmitChange(sut, CreateChangeRequest(year: year));

        // 23:00 at UTC-5 on 31 Dec 2031 is already 2032 in UTC.
        result.Reference.Should().Be("IYC-ORS-2032-00001");
    }

    [Fact]
    public async Task SubmitChangeRequestAsync_ConcurrentSubmissions_EachGetADifferentNumber()
    {
        var sut = BuildSut();

        var results = await Task.WhenAll(
            Enumerable.Range(0, 200).Select(_ => Task.Run(() => SubmitChange(sut, CreateChangeRequest())))
        );

        results
            .Select(r => r.Reference)
            .Should()
            .BeEquivalentTo(Enumerable.Range(1, 200).Select(n => $"IYC-ORS-2026-{n:D5}"));
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

        result.Reference.Should().Be("AP26EA5005001AAPL");
    }
}
