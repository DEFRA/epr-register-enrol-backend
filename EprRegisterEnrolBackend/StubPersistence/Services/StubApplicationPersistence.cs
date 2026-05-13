using EprRegisterEnrolBackend.StubPersistence.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.StubPersistence.Services;

public class StubApplicationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory)
    : MongoService<StubApplicationDocument>(connectionFactory, "stubAccreditationApplications", loggerFactory),
        IStubApplicationPersistence
{
    public async Task<IEnumerable<StubApplicationDocument>> GetByOrgAsync(string organisationId)
    {
        return await Collection.Find(d => d.OrganisationId == organisationId).ToListAsync();
    }

    public async Task<StubApplicationDocument?> GetByIdAsync(string organisationId, string stubApplicationId)
    {
        return await Collection
            .Find(d => d.OrganisationId == organisationId && d.StubApplicationId == stubApplicationId)
            .FirstOrDefaultAsync();
    }

    public async Task UpsertAsync(StubApplicationDocument document)
    {
        document.UpdatedAt = DateTime.UtcNow;

        var filter = Builders<StubApplicationDocument>.Filter.And(
            Builders<StubApplicationDocument>.Filter.Eq(d => d.OrganisationId, document.OrganisationId),
            Builders<StubApplicationDocument>.Filter.Eq(d => d.StubApplicationId, document.StubApplicationId));

        await Collection.ReplaceOneAsync(filter, document, new ReplaceOptions { IsUpsert = true });
    }

    protected override List<CreateIndexModel<StubApplicationDocument>> DefineIndexes(
        IndexKeysDefinitionBuilder<StubApplicationDocument> builder)
    {
        return
        [
            new CreateIndexModel<StubApplicationDocument>(
                builder.Combine(
                    builder.Ascending(d => d.OrganisationId),
                    builder.Ascending(d => d.StubApplicationId)),
                new CreateIndexOptions { Unique = true }),
            new CreateIndexModel<StubApplicationDocument>(
                builder.Ascending(d => d.OrganisationId))
        ];
    }
}
