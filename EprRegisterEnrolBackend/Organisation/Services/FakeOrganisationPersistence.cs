using EprRegisterEnrolBackend.Organisation.Models;

namespace EprRegisterEnrolBackend.Organisation.Services;

public class FakeOrganisationPersistence : IOrganisationPersistence
{
    private readonly List<OrganisationModel> _store = new();
    private readonly object _lock = new();

    public FakeOrganisationPersistence()
    {
        // Seed with some fake data similar to OperatorOrgDetailsViewModel
        _store.Add(new OrganisationModel
        {
            CompanyName = "Operator Export Company",
            CompaniesHouseNumber = "11044891",
            SchemeRegistrationId = "BN2712300000001",
            RegisteredAddress = "29 Acacia Road",
            ApprovedPerson = "General Blight",
            Directors = new List<DirectorModel>
            {
                new() { Name = "Eric Twinge" },
                new() { Name = "Crow" },
                new() { Name = "Doctor Gloom" }
            }
        });

        _store.Add(new OrganisationModel
        {
            CompanyName = "Another Company",
            CompaniesHouseNumber = "99999999",
            SchemeRegistrationId = "BN0000000000000",
            RegisteredAddress = "1 Example Street",
            ApprovedPerson = "Jane Example",
            Directors = new List<DirectorModel>
            {
                new() { Name = "Alice" }
            }
        });

        _store.Add(new OrganisationModel
        {
            CompanyName = "Third Company",
            CompaniesHouseNumber = "11111111",
            SchemeRegistrationId = "BN0000000000000",
            RegisteredAddress = "1 Example Street",
            ApprovedPerson = "Aysha Shaikh",
            Directors = new List<DirectorModel>
            {
                new() { Name = "Aysha" }
            }
        });

    }

    public Task<bool> CreateAsync(OrganisationModel organisation)
    {
        lock (_lock)
        {
            var exists = _store.Any(o =>
                string.Equals(o.CompanyName, organisation.CompanyName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.CompaniesHouseNumber, organisation.CompaniesHouseNumber, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(o.SchemeRegistrationId, organisation.SchemeRegistrationId, StringComparison.OrdinalIgnoreCase));

            if (exists) return Task.FromResult(false);

            _store.Add(organisation);
            return Task.FromResult(true);
        }
    }

    public Task<OrganisationModel?> GetByOrganisationName(string name)
    {
        lock (_lock)
        {
            var org = _store.FirstOrDefault(o => string.Equals(o.CompanyName, name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(org);
        }
    }

    public Task<IEnumerable<OrganisationModel>> GetAllAsync()
    {
        lock (_lock)
        {
            return Task.FromResult<IEnumerable<OrganisationModel>>(_store.ToList());
        }
    }

    public Task<IEnumerable<OrganisationModel>> SearchByValueAsync(string searchTerm)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
                return Task.FromResult<IEnumerable<OrganisationModel>>(_store.ToList());

            var term = searchTerm.Trim();
            var matches = _store.Where(o =>
                (o.CompanyName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.CompaniesHouseNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.SchemeRegistrationId?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.RegisteredAddress?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.ApprovedPerson?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (o.Directors != null && o.Directors.Any(d => d.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            ).ToList();

            return Task.FromResult<IEnumerable<OrganisationModel>>(matches);
        }
    }

    public Task<bool> UpdateAsync(OrganisationModel organisation)
    {
        lock (_lock)
        {
            var existing = _store.FirstOrDefault(o => string.Equals(o.CompanyName, organisation.CompanyName, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return Task.FromResult(false);

            existing.CompaniesHouseNumber = organisation.CompaniesHouseNumber;
            existing.SchemeRegistrationId = organisation.SchemeRegistrationId;
            existing.RegisteredAddress = organisation.RegisteredAddress;
            existing.ApprovedPerson = organisation.ApprovedPerson;
            existing.Directors = organisation.Directors;

            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(string name)
    {
        lock (_lock)
        {
            var existing = _store.FirstOrDefault(o => string.Equals(o.CompanyName, name, StringComparison.OrdinalIgnoreCase));
            if (existing is null) return Task.FromResult(false);

            _store.Remove(existing);
            return Task.FromResult(true);
        }
    }
}
