using EprRegisterEnrolBackend.Organisation.Models;

namespace EprRegisterEnrolBackend.Organisation.Services;

public class FakeOrganisationPersistence : IOrganisationPersistence
{
    private readonly List<OrganisationModel> _store = new();
    private readonly object _lock = new();

    public FakeOrganisationPersistence()
    {
        _store.Add(new OrganisationModel
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
                    Country = "England"
                }
            },
            ContactDetails = new ContactDetailsModel
            {
                FullName = "General Blight",
                Email = "general.blight@opexport.co.uk",
                Phone = "01234567890",
                Role = "Manager"
            },
            Users =
            [
                new PersonModel { FullName = "Eric Twinge", Role = "Director" },
                new PersonModel { FullName = "Crow", Role = "Director" },
                new PersonModel { FullName = "Doctor Gloom", Role = "Director" }
            ]
        });

        _store.Add(new OrganisationModel
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
                    Country = "Scotland"
                }
            },
            ContactDetails = new ContactDetailsModel
            {
                FullName = "Jane Example",
                Email = "jane@anothercompany.co.uk"
            },
            Users = [new PersonModel { FullName = "Alice", Role = "Director" }]
        });

        _store.Add(new OrganisationModel
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
                    Country = "Northern Ireland"
                }
            },
            ContactDetails = new ContactDetailsModel
            {
                FullName = "Aysha Shaikh",
                Email = "aysha@thirdcompany.co.uk"
            },
            Users = [new PersonModel { FullName = "Aysha", Role = "Director" }]
        });

        _store.Add(new OrganisationModel
        {
            OrgId = 50001,
            SchemaVersion = 1,
            Version = 1,
            BusinessType = "limited company",
            WasteProcessingTypes = ["reprocessor"],
            ReprocessingNations = ["england"],
            CompanyDetails = new CompanyDetailsModel
            {
                Name = "Glass Recycling Ltd",
                RegistrationNumber = "55001234",
                RegisteredAddress = new RegisteredAddressModel
                {
                    Line1 = "10 Glass Lane",
                    Town = "Manchester",
                    Postcode = "M1 1AA",
                    Country = "England"
                }
            },
            ContactDetails = new ContactDetailsModel
            {
                FullName = "Sam Glass",
                Email = "sam@glassrecycling.co.uk"
            },
            Users = [new PersonModel { FullName = "Sam Glass", Role = "Director" }],
            Registrations =
            [
                new RegistrationModel
                {
                    Status = "active",
                    Material = "Glass",
                    WasteProcessingType = "reprocessor",
                    SiteAddress = new SiteAddressModel
                    {
                        Line1 = "10 Glass Lane",
                        Town = "Manchester",
                        Postcode = "M1 1AA",
                        Country = "England"
                    }
                }
            ]
        });
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
            return Task.FromResult<IEnumerable<OrganisationSummaryModel>>(_store.Select(ToSummary).ToList());
        }
    }

    public Task<IEnumerable<OrganisationSummaryModel>> SearchByValueAsync(string searchTerm)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return Task.FromResult<IEnumerable<OrganisationSummaryModel>>(_store.Select(ToSummary).ToList());

            var term = searchTerm.Trim();
            var matches = _store.Where(o =>
                (o.CompanyDetails?.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.CompanyDetails?.TradingName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.CompanyDetails?.RegistrationNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.ContactDetails?.FullName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.ContactDetails?.Email?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            ).Select(ToSummary).ToList();

            return Task.FromResult<IEnumerable<OrganisationSummaryModel>>(matches);
        }
    }

    private static OrganisationSummaryModel ToSummary(OrganisationModel o) => new()
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
            if (index < 0) return Task.FromResult(false);

            _store[index] = organisation;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(int orgId)
    {
        lock (_lock)
        {
            var existing = _store.FirstOrDefault(o => o.OrgId == orgId);
            if (existing is null) return Task.FromResult(false);

            _store.Remove(existing);
            return Task.FromResult(true);
        }
    }
}
