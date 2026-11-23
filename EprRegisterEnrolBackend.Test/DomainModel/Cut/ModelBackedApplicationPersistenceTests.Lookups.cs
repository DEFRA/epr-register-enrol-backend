using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Test.DomainModel.Cut;

/// <summary>
/// The lookups, scoped to an organisation or a registration: each must find what the legacy query finds over the same
/// data, in the same order where the legacy query orders.
/// </summary>
public sealed partial class ModelBackedApplicationPersistenceTests
{
    private const string OtherOrganisationId = "6512a0f0c0ffee00000000b1";
    private const string GlassRegistrationId = "6512a0f0c0ffee0000000003";
    private const string OtherRegistrationId = "6512a0f0c0ffee00000000b2";

    private static readonly DateTime Day1 = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private sealed record LookupData(
        AccreditationApplicationModel Current,
        AccreditationApplicationModel Approved2026,
        AccreditationApplicationModel Withdrawn,
        AccreditationApplicationModel Glass,
        AccreditationApplicationModel OtherOrganisation
    );

    private static InterimSiteModel Interim(int siteId, string siteNumber) =>
        new()
        {
            SiteId = siteId,
            SiteNumber = siteNumber,
            Country = "Belgium",
            SiteName = "Interim " + siteNumber,
            AddressLine1 = "1 Quai",
            TownOrCity = "Antwerp",
            ContactName = "Contact",
            ContactEmail = "contact@example.com",
            ContactPhone = "000",
        };

    private static AccreditationApplicationModel Variant(
        DateTime createdAt,
        Action<AccreditationApplicationModel> configure,
        string organisationId = ReExOrganisationId
    )
    {
        var application = NewApplication(organisationId);
        application.CreatedAt = createdAt;
        application.UpdatedAt = createdAt;
        application.DateLastEdited = createdAt;
        application.OrgId = organisationId == ReExOrganisationId ? 500001 : 500027;
        configure(application);
        return application;
    }

    // Two organisations; the first holds a registration with a current, an approved 2026 and a withdrawn application,
    // and a glass registration; the second holds one application with a Case Management work item.
    private async Task<LookupData> LookupDataAsync()
    {
        var site = Site(1, "001");
        site.InterimSites = [Interim(1, "001"), Interim(2, "002")];
        InterimSiteSync.SyncMirror(site);

        var current = Variant(
            Day1.AddDays(2),
            a =>
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [site, Site(2, "002")],
                }
        );
        var approved = Variant(
            Day1,
            a =>
            {
                a.Year = 2026;
                a.ApplicationStatus = ApplicationStatus.Approved;
                a.AccreditationReference = "A26EX1234567P";
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [Site(3, "003")],
                };
            }
        );
        var withdrawn = Variant(
            Day1.AddDays(3),
            a =>
            {
                a.ApplicationStatus = ApplicationStatus.Withdrawn;
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [Site(4, "004")],
                };
            }
        );
        var glass = Variant(
            Day1.AddDays(1),
            a =>
            {
                a.RegistrationId = GlassRegistrationId;
                a.RegistrationReference = "R27ER7654321G";
                a.IsExporter = false;
                a.WasteProcessingType = "reprocessor";
                a.MaterialType = MaterialType.Glass;
                a.GlassRecyclingProcess = GlassRecyclingProcess.Remelt;
                a.OverseasSites = null;
            }
        );
        var other = Variant(
            Day1.AddDays(4),
            a =>
            {
                a.RegistrationId = OtherRegistrationId;
                a.RegistrationReference = "R27EX0000001P";
                a.OrganisationName = "Other Recycling Ltd";
                a.ApplicationStatus = ApplicationStatus.Submitted;
                a.CaseManagementWorkItemId = Guid.NewGuid();
                a.OverseasSites = new AccreditationApplicationOverseasSites
                {
                    Sites = [Site(1, "001")],
                };
            },
            OtherOrganisationId
        );

        foreach (var application in new[] { approved, current, withdrawn, glass, other })
        {
            await MigratedAsync(application);
        }

        return new LookupData(current, approved, withdrawn, glass, other);
    }

    private async Task<T> InSessionAsync<T>(Func<MongoDB.Driver.IClientSessionHandle, Task<T>> read)
    {
        using var session = await _factory.GetClient().StartSessionAsync(cancellationToken: Ct);
        return await read(session);
    }

    private static List<ObjectId?> Ids(IEnumerable<AccreditationApplicationModel> applications) =>
        [.. applications.Select(a => a.Id)];

    // --- by organisation ------------------------------------------------------------------------------------------

    [Fact]
    public async Task GetByOrganisation_FindsTheOrganisationsApplicationsNewestCreatedFirst()
    {
        await LookupDataAsync();

        var legacy = Ids(await _legacy.GetByOrganisationAsync(ReExOrganisationId));
        var model = Ids(await _model.GetByOrganisationAsync(ReExOrganisationId));

        Assert.Equal(4, legacy.Count);
        Assert.Equal(legacy, model);
    }

    // Its one caller, the migration, reads the organisation details from it. Legacy picks the application whose
    // document was written last, which includes the migration's own foreign-key stamps; in the model every application
    // of an organisation carries the same Organisation, so which one is newest can differ but the details can't.
    [Fact]
    public async Task GetNewestForOrganisation_HoldsTheSameOrganisationDetails()
    {
        await LookupDataAsync();

        var legacy = await InSessionAsync(s => _legacy.GetNewestForOrganisationAsync(ReExOrganisationId, s, Ct));
        var model = await InSessionAsync(s => _model.GetNewestForOrganisationAsync(ReExOrganisationId, s, Ct));

        Assert.Equal(ReExOrganisationId, model!.OrganisationId);
        Assert.Equal(legacy!.OrganisationName, model.OrganisationName);
        Assert.Equal(legacy.OrgId, model.OrgId);
        Assert.Equal(legacy.CompaniesHouseNumber, model.CompaniesHouseNumber);
        Assert.Equal(legacy.CompanyRegisteredAddress, model.CompanyRegisteredAddress);
        Assert.Equal(legacy.CompanyRegisterAddressPostcode, model.CompanyRegisterAddressPostcode);
        Assert.Equal(legacy.IsUkRegisteredAddress, model.IsUkRegisteredAddress);
        Assert.Equal(
            legacy.SubmitterContactDetails?.Email,
            model.SubmitterContactDetails?.Email
        );
    }

    // --- by registration ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(MaterialType.Plastic, 2027)]
    [InlineData(MaterialType.Plastic, 2026)]
    [InlineData(MaterialType.Steel, 2027)]
    [InlineData(MaterialType.Plastic, 2028)]
    public async Task GetLiveByRegistration_FindsTheSameApplication(MaterialType material, int year)
    {
        await LookupDataAsync();

        var legacy = await _legacy.GetLiveByRegistrationAsync(
            ReExOrganisationId,
            ReExRegistrationId,
            material,
            year
        );
        var model = await _model.GetLiveByRegistrationAsync(
            ReExOrganisationId,
            ReExRegistrationId,
            material,
            year
        );

        Assert.Equal(legacy?.Id, model?.Id);
    }

    [Fact]
    public async Task GetRegistrationGroup_FindsTheSameApplications()
    {
        await LookupDataAsync();

        var legacy = await InSessionAsync(s =>
            _legacy.GetRegistrationGroupAsync(ReExOrganisationId, ReExRegistrationId, 50, s, Ct)
        );
        var model = await InSessionAsync(s =>
            _model.GetRegistrationGroupAsync(ReExOrganisationId, ReExRegistrationId, 50, s, Ct)
        );

        Assert.Equal(3, legacy.Count);
        Assert.Equal(Ids(legacy).OrderBy(id => id), Ids(model).OrderBy(id => id));
    }

    [Fact]
    public async Task GetRegistrationGroup_KeepsToTheLimit()
    {
        await LookupDataAsync();

        var model = await InSessionAsync(s =>
            _model.GetRegistrationGroupAsync(ReExOrganisationId, ReExRegistrationId, 2, s, Ct)
        );

        Assert.Equal(2, model.Count);
    }

    // The allocation scope for new ORS ids and interim site numbers: every application under the registration,
    // withdrawn ones included.
    [Fact]
    public async Task GetOrsIdsByRegistration_FindsTheSameIds()
    {
        await LookupDataAsync();

        var legacy = await _legacy.GetOrsIdsByRegistrationAsync(ReExRegistrationId);
        var model = await _model.GetOrsIdsByRegistrationAsync(ReExRegistrationId);

        Assert.Contains("004", legacy);
        Assert.Equal(legacy.Order(), model.Order());
    }

    [Fact]
    public async Task GetInterimSiteNumbersByRegistration_FindsTheSameNumbers()
    {
        await LookupDataAsync();

        var legacy = await _legacy.GetInterimSiteNumbersByRegistrationAsync(ReExRegistrationId);
        var model = await _model.GetInterimSiteNumbersByRegistrationAsync(ReExRegistrationId);

        Assert.NotEmpty(legacy);
        Assert.Equal(legacy.Order(), model.Order());
    }

    [Theory]
    [InlineData(MaterialType.Plastic, 2027)]
    [InlineData(MaterialType.Plastic, 2026)]
    [InlineData(MaterialType.Glass, 2028)]
    public async Task GetLatestPreviousAccreditation_FindsTheSameApplication(
        MaterialType material,
        int beforeYear
    )
    {
        await LookupDataAsync();

        var legacy = await _legacy.GetLatestPreviousAccreditationAsync(
            ReExOrganisationId,
            ReExRegistrationId,
            material,
            beforeYear
        );
        var model = await _model.GetLatestPreviousAccreditationAsync(
            ReExOrganisationId,
            ReExRegistrationId,
            material,
            beforeYear
        );

        Assert.Equal(legacy?.Id, model?.Id);
    }

    // --- not scoped to an organisation ----------------------------------------------------------------------------

    [Fact]
    public async Task GetByCaseManagementWorkItemId_FindsTheSameApplication()
    {
        var data = await LookupDataAsync();
        var workItem = data.OtherOrganisation.CaseManagementWorkItemId!.Value;

        var legacy = await _legacy.GetByCaseManagementWorkItemIdAsync(workItem);
        var model = await _model.GetByCaseManagementWorkItemIdAsync(workItem);

        Assert.Equal(data.OtherOrganisation.Id, legacy!.Id);
        Assert.Equal(legacy.Id, model!.Id);
    }

    // RA-766's charge correction: open applications with a work item, Queried left out, in id order.
    [Fact]
    public async Task GetOpenNotQueriedWithCaseManagementWorkItem_FindsTheSameApplications()
    {
        var data = await LookupDataAsync();
        var queried = await MigratedAsync(
            Variant(
                Day1.AddDays(5),
                a =>
                {
                    a.RegistrationId = OtherRegistrationId;
                    a.RegistrationReference = "R27EX0000001P";
                    a.Year = 2028;
                    a.ApplicationStatus = ApplicationStatus.Queried;
                    a.CaseManagementWorkItemId = Guid.NewGuid();
                },
                OtherOrganisationId
            )
        );

        var legacy = await _legacy.GetOpenNotQueriedWithCaseManagementWorkItemAsync(Ct);
        var model = await _model.GetOpenNotQueriedWithCaseManagementWorkItemAsync(Ct);

        Assert.Equal([data.OtherOrganisation.Id], Ids(legacy));
        Assert.Equal(Ids(legacy), Ids(model));
        Assert.DoesNotContain(queried.Id, Ids(model));
    }

    // RA-768's overseas sites correction: every application with a work item, any status, in id
    // order.
    [Fact]
    public async Task GetWithCaseManagementWorkItem_FindsTheSameApplications()
    {
        var data = await LookupDataAsync();
        var withdrawn = await MigratedAsync(
            Variant(
                Day1.AddDays(5),
                a =>
                {
                    a.RegistrationId = OtherRegistrationId;
                    a.RegistrationReference = "R27EX0000001P";
                    a.Year = 2028;
                    a.ApplicationStatus = ApplicationStatus.Withdrawn;
                    a.CaseManagementWorkItemId = Guid.NewGuid();
                },
                OtherOrganisationId
            )
        );

        var legacy = await _legacy.GetWithCaseManagementWorkItemAsync(Ct);
        var model = await _model.GetWithCaseManagementWorkItemAsync(Ct);

        Assert.Equal([data.OtherOrganisation.Id, withdrawn.Id], Ids(legacy));
        Assert.Equal(Ids(legacy), Ids(model));
    }

    [Fact]
    public async Task GetByCaseManagementWorkItemId_Unknown_IsNull()
    {
        await LookupDataAsync();

        Assert.Null(await _model.GetByCaseManagementWorkItemIdAsync(Guid.NewGuid()));
    }

    [Theory]
    [InlineData("A26EX1234567P")]
    [InlineData("A27EX0000000P")]
    public async Task GetByAccreditationReference_FindsTheSameApplications(string reference)
    {
        await LookupDataAsync();

        var legacy = await _legacy.GetByAccreditationReferenceAsync(reference, Ct);
        var model = await _model.GetByAccreditationReferenceAsync(reference, Ct);

        Assert.Equal(Ids(legacy).OrderBy(id => id), Ids(model).OrderBy(id => id));
    }

    [Fact]
    public async Task GetOtherAccreditationReferenceHolders_FindsTheSameHolders()
    {
        var data = await LookupDataAsync();

        foreach (var excluded in new[] { data.Current, data.Approved2026 })
        {
            var legacy = await _legacy.GetOtherAccreditationReferenceHoldersAsync("A26EX1234567P", excluded.Id!.Value);
            var model = await _model.GetOtherAccreditationReferenceHoldersAsync("A26EX1234567P", excluded.Id!.Value);

            Assert.Equal(legacy.OrderBy(h => h.Id), model.OrderBy(h => h.Id));
        }
    }

    [Theory]
    [InlineData(500001, 2027)]
    [InlineData(500001, 2026)]
    [InlineData(500027, 2027)]
    [InlineData(599999, 2027)]
    public async Task GetByOrgIdAndYear_FindsTheSameApplicationsInIdOrder(int orgId, int year)
    {
        await LookupDataAsync();

        var legacy = await _legacy.GetByOrgIdAndYearAsync(orgId, year, Ct);
        var model = await _model.GetByOrgIdAndYearAsync(orgId, year, Ct);

        Assert.Equal(Ids(legacy), Ids(model));
    }

    // RA-764: the 2027 current and withdrawn plastic applications and the glass one; the 2026
    // approved one; another organisation's; none for an unknown reference.
    [Theory]
    [InlineData(2027, 3, "R27EX1234567P", "R27ER7654321G")]
    [InlineData(2027, 1, "R27EX0000001P")]
    [InlineData(2027, 1, "R27ER7654321G", "R26ER0000000G")]
    [InlineData(2026, 1, "R27EX1234567P")]
    [InlineData(2027, 0, "R26ER0000000G")]
    public async Task GetByRegistrationReferencesAndYear_FindsTheSameApplicationsInIdOrder(
        int year,
        int expected,
        params string[] registrationReferences
    )
    {
        await LookupDataAsync();

        var legacy = await _legacy.GetByRegistrationReferencesAndYearAsync(
            registrationReferences,
            year,
            Ct
        );
        var model = await _model.GetByRegistrationReferencesAndYearAsync(
            registrationReferences,
            year,
            Ct
        );

        Assert.Equal(expected, legacy.Count);
        Assert.Equal(Ids(legacy), Ids(model));
    }

    // The organisation number's unique index makes this one match at most.
    [Fact]
    public async Task FindOrganisationByNumber_FindsItsOrganisation()
    {
        await LookupDataAsync();

        var found = await _stores.Organisations.FindByOrganisationNumberAsync(500027, null, Ct);

        Assert.Equal(OtherOrganisationId, found!.ReExOrganisationId);
        Assert.Null(await _stores.Organisations.FindByOrganisationNumberAsync(599999, null, Ct));
    }
}
