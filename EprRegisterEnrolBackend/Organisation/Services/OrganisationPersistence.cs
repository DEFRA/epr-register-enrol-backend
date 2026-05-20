using EprRegisterEnrolBackend.Organisation.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Organisation.Services;

public interface IOrganisationPersistence
{
    public Task<bool> CreateAsync(OrganisationModel organisation);

    public Task<OrganisationModel?> GetByOrgIdAsync(int orgId);

    public Task<IEnumerable<OrganisationSummaryModel>> GetAllAsync();

    public Task<IEnumerable<OrganisationSummaryModel>> SearchByValueAsync(string searchTerm);

    public Task<bool> UpdateAsync(OrganisationModel organisation);

    public Task<bool> DeleteAsync(int orgId);

    public Task<bool> UpsertAsync(OrganisationModel organisation);
}

public class OrganisationPersistence(IMongoDbClientFactory connectionFactory, ILoggerFactory loggerFactory)
    : MongoService<OrganisationModel>(connectionFactory, "organisation_epr", loggerFactory), IOrganisationPersistence
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
            Logger.LogError(e, "Failed to insert {Organisation}", organisation);
            return false;
        }
    }

    public async Task<OrganisationModel?> GetByOrgIdAsync(int orgId)
    {
        var result = await Collection.Find(o => o.OrgId == orgId).FirstOrDefaultAsync();
        Logger.LogInformation("Searching for orgId {OrgId}, found {Result}", orgId, result);
        return result;
    }

    public async Task<IEnumerable<OrganisationSummaryModel>> GetAllAsync()
    {
        return await Collection
            .Find(Builders<OrganisationModel>.Filter.Empty)
            .Project<OrganisationSummaryModel>(SummaryProjection())
            .ToListAsync();
    }

    public async Task<IEnumerable<OrganisationSummaryModel>> SearchByValueAsync(string searchTerm)
    {
        var searchOptions = new TextSearchOptions { CaseSensitive = false, DiacriticSensitive = false };
        var filter = Builders<OrganisationModel>.Filter.Text(searchTerm, searchOptions);
        return await Collection
            .Find(filter)
            .Project<OrganisationSummaryModel>(SummaryProjection())
            .ToListAsync();
    }

    private static ProjectionDefinition<OrganisationModel> SummaryProjection() =>
        Builders<OrganisationModel>.Projection
            .Exclude(o => o.Users)
            .Exclude(o => o.Registrations)
            .Exclude(o => o.Accreditations);

    public async Task<bool> UpdateAsync(OrganisationModel organisation)
    {
        var filter = Builders<OrganisationModel>.Filter.Eq(o => o.OrgId, organisation.OrgId);
        var result = await Collection.ReplaceOneAsync(filter, organisation);
        return result.ModifiedCount > 0;
    }

    public async Task<bool> DeleteAsync(int orgId)
    {
        var result = await Collection.DeleteOneAsync(o => o.OrgId == orgId);
        return result.DeletedCount > 0;
    }

    public async Task<bool> UpsertAsync(OrganisationModel organisation)
    {
        var filter = Builders<OrganisationModel>.Filter.Eq(o => o.OrgId, organisation.OrgId);
        var result = await Collection.ReplaceOneAsync(filter, organisation,
            new ReplaceOptions { IsUpsert = true });
        return result.IsAcknowledged;
    }

    protected override List<CreateIndexModel<OrganisationModel>> DefineIndexes(
        IndexKeysDefinitionBuilder<OrganisationModel> builder)
    {
        var keys = Builders<OrganisationModel>.IndexKeys;
        return
        [
            new CreateIndexModel<OrganisationModel>(
                builder.Ascending(o => o.OrgId),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<OrganisationModel>(
                builder.Ascending("registrations.id")),
            new CreateIndexModel<OrganisationModel>(
                builder.Ascending("accreditations.id")),
            new CreateIndexModel<OrganisationModel>(
                keys.Combine(
                    keys.Ascending("registrations.material"),
                    keys.Ascending("registrations.wasteProcessingType"))),
            new CreateIndexModel<OrganisationModel>(
                builder.Ascending("registrations.status")),
            new CreateIndexModel<OrganisationModel>(
                builder.Ascending("accreditations.status")),
            new CreateIndexModel<OrganisationModel>(
                keys.Combine(
                    keys.Ascending("registrations.siteAddress.line1"),
                    keys.Ascending("registrations.siteAddress.postcode"),
                    keys.Ascending("registrations.material"),
                    keys.Ascending("registrations.wasteProcessingType"))),
            new CreateIndexModel<OrganisationModel>(
                builder.Ascending("registrations.approvedPersons.email"))
        ];
    }
}
