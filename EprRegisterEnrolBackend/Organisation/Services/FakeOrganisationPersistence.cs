using EprRegisterEnrolBackend.Organisation.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.Organisation.Services;

// In-memory ReEx organisation fixtures for StubReExApiAdapter's dev-mode responses.
public class FakeOrganisationPersistence
{
    public static readonly ObjectId Reg50001 = ObjectId.Parse("aaa000000000000000050001");
    public static readonly ObjectId Reg50002 = ObjectId.Parse("aaa000000000000000050002");
    public static readonly ObjectId Reg50003 = ObjectId.Parse("aaa000000000000000050003");
    public static readonly ObjectId Reg50005 = ObjectId.Parse("aaa000000000000000050005");
    public static readonly ObjectId Reg50006 = ObjectId.Parse("aaa000000000000000050006");
    public static readonly ObjectId Reg50013 = ObjectId.Parse("aaa000000000000000050013");

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
                BusinessType = "unincorporated",
                WasteProcessingTypes = ["reprocessor", "exporter"],
                ReprocessingNations = ["england", "wales"],
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
                    new PersonModel { FullName = "Eric Twinge", Role = "Director" },
                    new PersonModel { FullName = "Crow", Role = "Director" },
                    new PersonModel { FullName = "Doctor Gloom", Role = "Director" },
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
                WasteProcessingTypes = ["reprocessor"],
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
                Users = [new PersonModel { FullName = "Alice", Role = "Director" }],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 3,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = "individual",
                WasteProcessingTypes = ["exporter"],
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
                Users = [new PersonModel { FullName = "Aysha", Role = "Director" }],
            }
        );

        _store.Add(
            new OrganisationModel
            {
                OrgId = 50001,
                SchemaVersion = 1,
                Version = 1,
                BusinessType = "unincorporated",
                WasteProcessingTypes = ["reprocessor"],
                ReprocessingNations = ["england"],
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
                        Status = "created",
                        Material = "plastic",
                        WasteProcessingType = "reprocessor",
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
                BusinessType = "unincorporated",
                WasteProcessingTypes = ["reprocessor"],
                ReprocessingNations = ["england"],
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
                        Status = "created",
                        Material = "glass",
                        // RA-307: local-dev/e2e coverage for the "Glass - Remelt"
                        // display suffix, mapped to the enum by StubReExApiAdapter.
                        GlassRecyclingProcess = "glass_re_melt",
                        WasteProcessingType = "reprocessor",
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
                BusinessType = "unincorporated",
                WasteProcessingTypes = ["exporter"],
                ReprocessingNations = ["england"],
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
                        Status = "created",
                        Material = "plastic",
                        WasteProcessingType = "exporter",
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
                BusinessType = "unincorporated",
                WasteProcessingTypes = ["exporter"],
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
                        Status = "created",
                        Material = "glass",
                        WasteProcessingType = "exporter",
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
                BusinessType = "unincorporated",
                WasteProcessingTypes = ["exporter"],
                ReprocessingNations = ["england"],
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
                        Status = "created",
                        Material = "plastic",
                        WasteProcessingType = "exporter",
                        OverseasSites = ["900010", "900011"],
                        WasteManagementPermits =
                        [
                            new WasteManagementPermitModel { PermitNumber = "WML50013" },
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
