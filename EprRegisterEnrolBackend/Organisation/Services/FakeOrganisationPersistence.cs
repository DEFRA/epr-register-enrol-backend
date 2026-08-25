using EprRegisterEnrolBackend.Organisation.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Organisation.Services;

// In-memory ReEx organisation fixtures for StubReExApiAdapter's dev-mode responses.
public class FakeOrganisationPersistence
{
    private const string BusinessTypeUnincorporated = "unincorporated";
    private const string WasteProcessingTypeExporter = "exporter";
    private const string WasteProcessingTypeReprocessor = "reprocessor";
    private const string NationEngland = "england";
    private const string RoleDirector = "Director";
    private const string RegistrationStatusCreated = "created";

    public static readonly ObjectId Reg50001 = ObjectId.Parse("aaa000000000000000050001");
    public static readonly ObjectId Reg50002 = ObjectId.Parse("aaa000000000000000050002");
    public static readonly ObjectId Reg50003 = ObjectId.Parse("aaa000000000000000050003");
    public static readonly ObjectId Reg50005 = ObjectId.Parse("aaa000000000000000050005");
    public static readonly ObjectId Reg50006 = ObjectId.Parse("aaa000000000000000050006");
    public static readonly ObjectId Reg50013 = ObjectId.Parse("aaa000000000000000050013");
    public static readonly ObjectId Reg50014 = ObjectId.Parse("aaa000000000000000050014");
    public static readonly ObjectId Reg50015 = ObjectId.Parse("aaa000000000000000050015");

    private readonly List<OrganisationModel> _store = new();
    private readonly object _lock = new();

    public FakeOrganisationPersistence()
    {
        _store.Add(
            new OrganisationModel
            {
                OrgId = 1,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes =
                [
                    WasteProcessingTypeReprocessor,
                    WasteProcessingTypeExporter,
                ],
                ReprocessingNations = [NationEngland, "wales"],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Operator Export Company",
                    TradingName = "Op Export Co",
                    RegistrationNumber = "11044891",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "29 Acacia Road",
                        Town = "London",
                        Postcode = "SW1A 1AA",
                        Country = "England",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "General Blight",
                    Email = "general.blight@opexport.co.uk",
                    Phone = "01234567890",
                    Role = "Manager",
                },
                Users =
                [
                    new PersonModel { FullName = "Eric Twinge", Role = RoleDirector },
                    new PersonModel { FullName = "Crow", Role = RoleDirector },
                    new PersonModel { FullName = "Doctor Gloom", Role = RoleDirector },
                ],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 2,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = "partnership",
                WasteProcessingTypes = [WasteProcessingTypeReprocessor],
                ReprocessingNations = ["scotland"],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Another Company",
                    RegistrationNumber = "99999999",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "1 Example Street",
                        Town = "Edinburgh",
                        Postcode = "EH1 1AA",
                        Country = "Scotland",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Jane Example",
                    Email = "jane@anothercompany.co.uk",
                },
                Users = [new PersonModel { FullName = "Alice", Role = RoleDirector }],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 3,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = "individual",
                WasteProcessingTypes = [WasteProcessingTypeExporter],
                ReprocessingNations = ["northern_ireland"],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Third Company",
                    RegistrationNumber = "11111111",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "1 Example Street",
                        Town = "Belfast",
                        Postcode = "BT1 1AA",
                        Country = "Northern Ireland",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Aysha Shaikh",
                    Email = "aysha@thirdcompany.co.uk",
                },
                Users = [new PersonModel { FullName = "Aysha", Role = RoleDirector }],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 50001,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes = [WasteProcessingTypeReprocessor],
                ReprocessingNations = [NationEngland],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "NEWDEV RECYCLING LIMITED",
                    TradingName = "NEWDEV RECYCLING LIMITED",
                    RegistrationNumber = "R26ER5000390068PL",
                    CompaniesHouseNumber = "12345001",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "UNIT 5",
                        Town = "Bolton",
                        Postcode = "BL4 7AQ",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Site Manager",
                    Email = "info@newdevrecycling.co.uk",
                },
                Users = [],
                Accreditations = [],
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = Reg50001,
                        SiteId = "REG001",
                        Status = RegistrationStatusCreated,
                        Material = "plastic",
                        WasteProcessingType = WasteProcessingTypeReprocessor,
                        SiteAddress = new SiteAddressModel
                        {
                            Line1 = "UNIT 5",
                            Town = "Bolton",
                            Postcode = "BL4 7AQ",
                        },
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50001" },
                        ],
                    },
                ],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 50002,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes = [WasteProcessingTypeReprocessor],
                ReprocessingNations = [NationEngland],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Beta Recycling Co",
                    TradingName = "Beta Recycling Co",
                    RegistrationNumber = "R26ER5000390068PL",
                    CompaniesHouseNumber = "12345002",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "Site Lane 002",
                        Town = "Siteville",
                        Postcode = "SIT3 OO2",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Site Manager",
                    Email = "info@betarecycling.co.uk",
                },
                Users = [],
                Accreditations = [],
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = Reg50002,
                        SiteId = "REG002",
                        Status = RegistrationStatusCreated,
                        Material = "glass",
                        // RA-307: local-dev/e2e coverage for the "Glass - Remelt"
                        // display suffix, mapped to the enum by StubReExApiAdapter.
                        GlassRecyclingProcess = "glass_re_melt",
                        WasteProcessingType = WasteProcessingTypeReprocessor,
                        SiteAddress = new SiteAddressModel
                        {
                            Line1 = "Site Lane 002",
                            Town = "Siteville",
                            Postcode = "SIT3 OO2",
                        },
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50002" },
                        ],
                    },
                ],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 50005,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes = [WasteProcessingTypeExporter],
                ReprocessingNations = [NationEngland],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Export Plastics Ltd",
                    TradingName = "Export Plastics",
                    RegistrationNumber = "EXP-50005",
                    CompaniesHouseNumber = "12345005",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "Export House",
                        Town = "Southampton",
                        Postcode = "SO14 2AQ",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Export Manager",
                    Email = "info@exportplastics.co.uk",
                },
                Users = [],
                Accreditations = [],
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = Reg50005,
                        SiteId = "REG005",
                        Status = RegistrationStatusCreated,
                        Material = "plastic",
                        WasteProcessingType = WasteProcessingTypeExporter,
                        OverseasSites = ["900010", "900011"],
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50005" },
                        ],
                    },
                ],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 50006,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes = [WasteProcessingTypeExporter],
                ReprocessingNations = ["scotland"],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Global Glass Exports",
                    TradingName = "Global Glass",
                    RegistrationNumber = "EXP-50006",
                    CompaniesHouseNumber = "12345006",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "Harbour View",
                        Town = "Wick",
                        Postcode = "KW2 7LZ",
                        Country = "Scotland",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Glass Export Manager",
                    Email = "info@globalglassexports.co.uk",
                },
                Users = [],
                Accreditations = [],
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = Reg50006,
                        SiteId = "REG006",
                        Status = RegistrationStatusCreated,
                        Material = "glass",
                        WasteProcessingType = WasteProcessingTypeExporter,
                        OverseasSites = ["900001", "900002", "900003", "900004"],
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50006A" },
                            new WasteManagementPermitModel { PermitNumber = "WML50006B" },
                        ],
                    },
                ],
            }
        );

        // RA-297 regression-guard org: exists only so interim-site.e2e.js has a Plastic
        // exporter fixture it doesn't share with exporter-accreditation.e2e.js. Both specs
        // used to target org 50005's "Exporter accreditation — Plastic" journey; when run
        // concurrently under wdio, both could hit AccreditationApplicationEndpoints.Seed's
        // read-then-create check before either had written its result, each pass the "no
        // existing application" check, and both create a live application for the same
        // (org, registrationId, materialType, year) — Seed has no transaction and no unique
        // index (MongoService.EnsureIndexes never actually runs — see its commented-out
        // Collection.Indexes.CreateMany call). Whichever duplicate a later
        // resolveLandingApplication() list-and-pick call landed on then depended on Mongo's
        // ObjectId tiebreak, not on which one the test had actually progressed — causing
        // exporter-accreditation.e2e.js's later tests to read back a fresh, untouched
        // duplicate instead of the one it had submitted. Giving interim-site.e2e.js its own
        // org removes the only other caller of that Seed path for 50005, without touching
        // Seed's own concurrency behaviour.
        _store.Add(
            new OrganisationModel
            {
                OrgId = 50013,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes = [WasteProcessingTypeExporter],
                ReprocessingNations = [NationEngland],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Interim Site Test Exports Ltd",
                    TradingName = "Interim Site Test Exports",
                    RegistrationNumber = "EXP-50013",
                    CompaniesHouseNumber = "12345013",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "Export House",
                        Town = "Southampton",
                        Postcode = "SO14 2AQ",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Export Manager",
                    Email = "info@interimsitetestexports.co.uk",
                },
                Users = [],
                Accreditations = [],
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = Reg50013,
                        SiteId = "REG013",
                        Status = RegistrationStatusCreated,
                        Material = "plastic",
                        WasteProcessingType = WasteProcessingTypeExporter,
                        OverseasSites = ["900010", "900011"],
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50013" },
                        ],
                    },
                ],
            }
        );

        // RA-477 regression-guard org: exists for the same reason as org 50013 above —
        // ors-fee-calculation.e2e.js needs to add ORS/interim sites to a Plastic exporter
        // application and assert an exact site count on the payment page, which is exactly
        // the shape of assertion the org-50005 Seed race (documented above) corrupts under
        // concurrent wdio workers. Giving it its own org sidesteps that race entirely rather
        // than relying on timing.
        //
        // Deliberately NOT copying org 50013's OverseasSites = ["900010", "900011"] pattern:
        // StubReExApiAdapter.cs converts each RegistrationModel.OverseasSites entry into a
        // pre-seeded, pre-selected OverseasSiteModel at accreditation-creation time. Org 50013
        // never noticed because its own test never asserts an absolute site count — but this
        // org's whole purpose IS an absolute count assertion, so it must start with zero
        // pre-seeded sites or "2 added via the wizard" silently becomes 4.
        _store.Add(
            new OrganisationModel
            {
                OrgId = 50014,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes = [WasteProcessingTypeExporter],
                ReprocessingNations = [NationEngland],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "ORS Fee Test Exports Ltd",
                    TradingName = "ORS Fee Test Exports",
                    RegistrationNumber = "EXP-50014",
                    CompaniesHouseNumber = "12345014",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "Export House",
                        Town = "Southampton",
                        Postcode = "SO14 2AQ",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Export Manager",
                    Email = "info@orsfeetestexports.co.uk",
                },
                Users = [],
                Accreditations = [],
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = Reg50014,
                        SiteId = "REG014",
                        Status = RegistrationStatusCreated,
                        Material = "plastic",
                        WasteProcessingType = WasteProcessingTypeExporter,
                        OverseasSites = [],
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50014" },
                        ],
                    },
                ],
            }
        );

        // RA-481 regression-guard org: exists for the same reason as orgs 50013 and 50014
        // above — exporter-accreditation.e2e.js's "Exporter Accreditation - Full Journey
        // (Plastic 2027)" describe block used to run its entire suite of tests (adding ORS/
        // interim sites, submitting the application, navigating back and forth) against
        // shared org 50005, which is exactly the shape of repeated, cross-test reuse the
        // org-50005 Seed race (documented above) corrupts under concurrent wdio workers.
        // RA-481 made this newly observable: locking a Submitted application read-only means
        // a test landing on the "wrong" duplicate (an untouched, isExporter-derived-correctly
        // but otherwise-fresh copy) now visibly diverges from the one earlier tests actually
        // progressed, instead of just silently tolerating two equally-editable copies as
        // before. Giving the whole spec its own org sidesteps the race entirely rather than
        // relying on test ordering within a shared one.
        //
        // Mirrors org 50005's OverseasSites = ["900010", "900011"] (Germany/France) rather
        // than org 50014's empty-list pattern: this spec's pre-RA-481 tests already assumed a
        // couple of pre-seeded, pre-accredited sites exist (e.g. continuing past the overseas-
        // sites task without having added one yet), so starting empty would change behaviour
        // this fix is not meant to touch.
        _store.Add(
            new OrganisationModel
            {
                OrgId = 50015,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = BusinessTypeUnincorporated,
                WasteProcessingTypes = [WasteProcessingTypeExporter],
                ReprocessingNations = [NationEngland],
                CompanyDetails = new CompanyDetailsModel
                {
                    Name = "Exporter Accreditation Test Exports Ltd",
                    TradingName = "Exporter Accreditation Test Exports",
                    RegistrationNumber = "EXP-50015",
                    CompaniesHouseNumber = "12345015",
                    RegisteredAddress = new RegisteredAddressModel
                    {
                        Line1 = "Export House",
                        Town = "Southampton",
                        Postcode = "SO14 2AQ",
                    },
                },
                ContactDetails = new ContactDetailsModel
                {
                    FullName = "Export Manager",
                    Email = "info@exporteraccreditationtestexports.co.uk",
                },
                Users = [],
                Accreditations = [],
                Registrations =
                [
                    new RegistrationModel
                    {
                        Id = Reg50015,
                        SiteId = "REG015",
                        Status = RegistrationStatusCreated,
                        Material = "plastic",
                        WasteProcessingType = WasteProcessingTypeExporter,
                        OverseasSites = ["900010", "900011"],
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50015" },
                        ],
                    },
                ],
            }
        );
    }

    public Task<bool> CreateAsync(OrganisationModel organisation)
    {
        lock (_lock)
        {
            if (_store.Any(o => o.OrgId == organisation.OrgId))
                return Task.FromResult(false);

            _store.Add(organisation);
            return Task.FromResult(true);
        }
    }

    public Task<OrganisationModel?> GetByOrgIdAsync(int orgId)
    {
        lock (_lock)
        {
            return Task.FromResult(_store.FirstOrDefault(o => o.OrgId == orgId));
        }
    }

    public Task<IEnumerable<OrganisationSummaryModel>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<OrganisationSummaryModel>>(
                _store.Select(ToSummary).ToList()
            );
        }
    }

    public Task<IEnumerable<OrganisationSummaryModel>> SearchByValueAsync(string searchTerm)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return Task.FromResult<IEnumerable<OrganisationSummaryModel>>(
                    _store.Select(ToSummary).ToList()
                );

            var term = searchTerm.Trim();
            var matches = _store
                .Where(o =>
                    (
                        o.CompanyDetails?.Name?.Contains(term, StringComparison.OrdinalIgnoreCase)
                        ?? false
                    )
                    || (
                        o.CompanyDetails?.TradingName?.Contains(
                            term,
                            StringComparison.OrdinalIgnoreCase
                        ) ?? false
                    )
                    || (
                        o.CompanyDetails?.RegistrationNumber?.Contains(
                            term,
                            StringComparison.OrdinalIgnoreCase
                        ) ?? false
                    )
                    || (
                        o.ContactDetails?.FullName?.Contains(
                            term,
                            StringComparison.OrdinalIgnoreCase
                        ) ?? false
                    )
                    || (
                        o.ContactDetails?.Email?.Contains(term, StringComparison.OrdinalIgnoreCase)
                        ?? false
                    )
                )
                .Select(ToSummary)
                .ToList();

            return Task.FromResult<IEnumerable<OrganisationSummaryModel>>(matches);
        }
    }

    private static OrganisationSummaryModel ToSummary(OrganisationModel o) =>
        new()
        {
            OrgId = o.OrgId,
            WasteProcessingTypes = o.WasteProcessingTypes,
            ReprocessingNations = o.ReprocessingNations,
            BusinessType = o.BusinessType,
            CompanyDetails = o.CompanyDetails,
            Partnership = o.Partnership,
            ContactDetails = o.ContactDetails,
            SubmittedToRegulator = o.SubmittedToRegulator,
        };

    public Task<bool> UpdateAsync(OrganisationModel organisation)
    {
        lock (_lock)
        {
            var index = _store.FindIndex(o => o.OrgId == organisation.OrgId);
            if (index < 0)
                return Task.FromResult(false);

            _store[index] = organisation;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(int orgId)
    {
        lock (_lock)
        {
            var existing = _store.FirstOrDefault(o => o.OrgId == orgId);
            if (existing is null)
                return Task.FromResult(false);

            _store.Remove(existing);
            return Task.FromResult(true);
        }
    }

    public Task<bool> UpsertAsync(OrganisationModel organisation)
    {
        lock (_lock)
        {
            var idx = _store.FindIndex(o => o.OrgId == organisation.OrgId);
            if (idx >= 0)
                _store[idx] = organisation;
            else
                _store.Add(organisation);
            return Task.FromResult(true);
        }
    }
}
