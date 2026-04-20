using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Organisation.Services;

public interface IOrganisationPersistence
{
    public Task<bool> CreateAsync(OrganisationModel organisation);

    public Task<OrganisationModel?> GetByOrganisationName(string name);

    public Task<IEnumerable<OrganisationModel>> GetAllAsync();

    public Task<IEnumerable<OrganisationModel>> SearchByValueAsync(string searchTerm);

    public Task<bool> UpdateAsync(OrganisationModel organisation);

    public Task<bool> DeleteAsync(string name);
}

public class OrganisationPersistence(IMongoDbClientFactory connectionFactory, ILoggerFactory loggerFactory)
    : MongoService<OrganisationModel>(connectionFactory, "organisation", loggerFactory), IOrganisationPersistence
{
    public async Task<bool> CreateAsync(OrganisationModel organisation)
    {
        try
        {
            await Collection.InsertOneAsync(organisation);
            return true;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to insert {organisation}", organisation);
            return false;
        }
    }

    public async Task<OrganisationModel?> GetByOrganisationName(string name)
    {
        var result = await Collection.Find(b => b.CompanyName == name).FirstOrDefaultAsync();
        Logger.LogInformation("Searching for {CompanyName}, found {Result}", name, result);
        return result;
    }

    public async Task<IEnumerable<OrganisationModel>> GetAllAsync()
    {
        return await Collection.Find(_ => true).ToListAsync();
    }

    public async Task<IEnumerable<OrganisationModel>> SearchByValueAsync(string searchTerm)
    {
        var searchOptions = new TextSearchOptions { CaseSensitive = false, DiacriticSensitive = false };
        var filter = Builders<OrganisationModel>.Filter.Text(searchTerm, searchOptions);
        var result = await Collection.Find(filter).ToListAsync();
        return result;
    }

    public async Task<bool> UpdateAsync(OrganisationModel organisation)
    {
        var filter = Builders<OrganisationModel>.Filter.Eq(e => e.CompanyName, organisation.CompanyName);
        var update = Builders<OrganisationModel>.Update
            .Set(e => e.CompaniesHouseNumber, organisation.CompaniesHouseNumber)
            .Set(e => e.SchemeRegistrationId, organisation.SchemeRegistrationId)
            .Set(e => e.RegisteredAddress, organisation.RegisteredAddress)
            .Set(e => e.ApprovedPerson, organisation.ApprovedPerson)
            .Set(e => e.Directors, organisation.Directors);

        var result = await Collection.UpdateOneAsync(filter, update);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> DeleteAsync(string name)
    {
        var result = await Collection.DeleteOneAsync(e => e.CompanyName == name);
        return result.DeletedCount > 0;
    }

    protected override List<CreateIndexModel<OrganisationModel>> DefineIndexes(
        IndexKeysDefinitionBuilder<OrganisationModel> builder)
    {
        var options = new CreateIndexOptions { Unique = true };
        var nameIndex = new CreateIndexModel<OrganisationModel>(builder.Ascending(e => e.CompanyName), options);
        var regIndex = new CreateIndexModel<OrganisationModel>(builder.Ascending(e => e.CompaniesHouseNumber), options);
        var schemeIndex = new CreateIndexModel<OrganisationModel>(builder.Ascending(e => e.SchemeRegistrationId), options);
        return new List<CreateIndexModel<OrganisationModel>> { nameIndex, regIndex, schemeIndex };
    }
}
