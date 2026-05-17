using EprRegisterEnrolBackend.Organisation.Models;

namespace EprRegisterEnrolBackend.Organisation.Services;

public class FallbackOrganisationPersistence(
    OrganisationPersistence mongo,
    FakeOrganisationPersistence fake) : IOrganisationPersistence
{
    public async Task<OrganisationModel?> GetByOrgIdAsync(int orgId)
    {
        var result = await mongo.GetByOrgIdAsync(orgId);
        if (result is not null)
            return result;

        var fromFake = await fake.GetByOrgIdAsync(orgId);
        if (fromFake is not null)
            await mongo.CreateAsync(fromFake);

        return fromFake;
    }

    public Task<bool> CreateAsync(OrganisationModel organisation) => mongo.CreateAsync(organisation);
    public Task<IEnumerable<OrganisationSummaryModel>> GetAllAsync() => mongo.GetAllAsync();
    public Task<IEnumerable<OrganisationSummaryModel>> SearchByValueAsync(string searchTerm) => mongo.SearchByValueAsync(searchTerm);
    public Task<bool> UpdateAsync(OrganisationModel organisation) => mongo.UpdateAsync(organisation);
    public Task<bool> DeleteAsync(int orgId) => mongo.DeleteAsync(orgId);
}
