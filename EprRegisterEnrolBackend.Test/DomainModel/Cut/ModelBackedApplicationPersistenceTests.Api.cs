using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// The service-client accreditation API's queries (B13): after the cut they find granted accreditations in the model,
/// which also ends the API's lookup through legacy (decision A of RA-705, accepted as phase 6 work).
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private const string UnservableOrganisationId = "6512a0f0c0ffee00000000c1";
    private const int FirstYear = 2026;

    // The lookup data, plus a second organisation's approved 2027 accreditation and an approved application the API
    // can't serve: no organisation number, no registration number and no accreditation number.
    private async Task<LookupData> ApiDataAsync()
    {
        var data = await LookupDataAsync();
        await MigratedAsync(
            Variant(
                Day1.AddDays(5),
                a =>
                {
                    a.RegistrationId = OtherRegistrationId;
                    a.RegistrationReference = "R27EX0000001P";
                    a.OrganisationName = "Other Recycling Ltd";
                    a.ApplicationStatus = ApplicationStatus.Approved;
                    a.AccreditationReference = "A27EX0000001P";
                    a.DateLastEdited = Day1.AddDays(6);
                    a.OverseasSites = new AccreditationApplicationOverseasSites
                    {
                        Sites = [Site(5, "005")],
                    };
                },
                OtherOrganisationId
            )
        );
        await MigratedAsync(
            Variant(
                Day1.AddDays(6),
                a =>
                {
                    a.RegistrationId = "6512a0f0c0ffee00000000c2";
                    a.RegistrationReference = null;
                    a.OrganisationName = "Unnumbered Ltd";
                    a.OrgId = null;
                    a.ApplicationStatus = ApplicationStatus.Approved;
                    a.AccreditationReference = null;
                    a.OverseasSites = null;
                },
                UnservableOrganisationId
            )
        );
        return data;
    }

    [Theory]
    [InlineData(500001, "R27EX1234567P", 2026)]
    [InlineData(500001, "R27EX1234567P", 2027)]
    [InlineData(500027, "R27EX0000001P", 2027)]
    [InlineData(500027, "R27EX1234567P", 2027)]
    public async Task GetGrantedAccreditation_FindsTheSameApplication(
        int orgId,
        string registration,
        int year
    )
    {
        await ApiDataAsync();

        var legacy = await _legacy.GetGrantedAccreditationAsync(
            orgId,
            registration,
            year,
            FirstYear
        );
        var model = await _model.GetGrantedAccreditationAsync(orgId, registration, year, FirstYear);

        Assert.Equal(legacy?.Id, model?.Id);
    }

    [Fact]
    public async Task GetGrantedAccreditation_BeforeTheFirstYear_IsNull()
    {
        await ApiDataAsync();

        Assert.Null(await _model.GetGrantedAccreditationAsync(500001, "R27EX1234567P", 2026, 2027));
    }

    public static TheoryData<int?, int?, string?, int, int> SearchCases =>
        new()
        {
            { null, null, null, 0, 10 },
            { 2026, null, null, 0, 10 },
            { 2027, null, null, 0, 10 },
            { null, 500027, null, 0, 10 },
            { null, null, "R27EX1234567P", 0, 10 },
            // An empty registration number still filters, as legacy does, so finds nothing.
            { null, null, "", 0, 10 },
            { null, null, null, 1, 1 },
            { null, null, null, 5, 10 },
        };

    [Theory]
    [MemberData(nameof(SearchCases))]
    public async Task SearchGrantedAccreditations_FindsTheSamePageAndTotal(
        int? year,
        int? organisationNumber,
        string? registrationNumber,
        int skip,
        int limit
    )
    {
        await ApiDataAsync();
        var filter = new GrantedAccreditationFilter(year, organisationNumber, registrationNumber);

        var (legacyItems, legacyTotal) = await _legacy.SearchGrantedAccreditationsAsync(
            filter,
            FirstYear,
            skip,
            limit
        );
        var (modelItems, modelTotal) = await _model.SearchGrantedAccreditationsAsync(
            filter,
            FirstYear,
            skip,
            limit
        );

        Assert.Equal(legacyTotal, modelTotal);
        Assert.Equal(Ids(legacyItems), Ids(modelItems));
    }

    [Fact]
    public async Task CountUnservableApproved_CountsTheSame()
    {
        await ApiDataAsync();

        var legacy = await _legacy.CountUnservableApprovedAsync(FirstYear, Ct);
        var model = await _model.CountUnservableApprovedAsync(FirstYear, Ct);

        Assert.Equal(new UnservableApprovedCounts(1, 1, 1), legacy);
        Assert.Equal(legacy, model);
    }
}
