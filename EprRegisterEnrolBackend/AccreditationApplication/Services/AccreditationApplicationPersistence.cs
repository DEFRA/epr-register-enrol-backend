using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class AccreditationApplicationPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory
)
    : MongoService<AccreditationApplicationModel>(
        connectionFactory,
        "accreditationApplications",
        loggerFactory
    ),
        IAccreditationApplicationPersistence
{
    public async Task<AccreditationApplicationModel?> CreateAsync(
        AccreditationApplicationModel application
    )
    {
        try
        {
            await Collection.InsertOneAsync(application);
            return application;
        }
        catch (Exception e)
        {
            Logger.LogError(
                e,
                "Failed to insert accreditation application for org={OrganisationId}",
                application.OrganisationId
            );
            return null;
        }
    }

    public async Task<IEnumerable<AccreditationApplicationModel>> GetByOrganisationAsync(
        string organisationId
    )
    {
        return await Collection.Find(a => a.OrganisationId == organisationId).ToListAsync();
    }

    public async Task<AccreditationApplicationModel?> GetByIdAsync(
        string organisationId,
        string applicationId
    )
    {
        if (!ObjectId.TryParse(applicationId, out var objectId))
            return null;

        return await Collection
            .Find(a => a.OrganisationId == organisationId && a.Id == objectId)
            .FirstOrDefaultAsync();
    }

    public async Task<AccreditationApplicationModel?> GetByCaseManagementWorkItemIdAsync(
        Guid workItemId
    )
    {
        return await Collection
            .Find(a => a.CaseManagementWorkItemId == workItemId)
            .FirstOrDefaultAsync();
    }

    public async Task<AccreditationApplicationModel?> UpdateAsync(
        AccreditationApplicationModel application
    )
    {
        if (application.Id is null)
            return null;

        var filter = Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, application.Id);

        application.UpdatedAt = DateTime.UtcNow;

        var result = await Collection.ReplaceOneAsync(filter, application);
        return result.ModifiedCount > 0 ? application : null;
    }

    public async Task<AccreditationApplicationModel?> UpdateIfOrsIdAbsentAsync(
        AccreditationApplicationModel application,
        string orsId
    )
    {
        if (application.Id is null)
            return null;

        var filter = Builders<AccreditationApplicationModel>.Filter.And(
            Builders<AccreditationApplicationModel>.Filter.Eq(a => a.Id, application.Id),
            Builders<AccreditationApplicationModel>.Filter.Not(
                Builders<AccreditationApplicationModel>.Filter.ElemMatch(
                    a => a.OverseasSites!.Sites,
                    s => s.OrsId == orsId
                )
            )
        );

        application.UpdatedAt = DateTime.UtcNow;

        var result = await Collection.ReplaceOneAsync(filter, application);
        return result.ModifiedCount > 0 ? application : null;
    }

    protected override List<CreateIndexModel<AccreditationApplicationModel>> DefineIndexes(
        IndexKeysDefinitionBuilder<AccreditationApplicationModel> builder
    )
    {
        return
        [
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.OrganisationId)
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.ApplicationStatus)
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.MaterialType)
            ),
            new CreateIndexModel<AccreditationApplicationModel>(builder.Ascending(a => a.Year)),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.SourceReExAccreditationId)
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Combine(
                    builder.Ascending(a => a.OrganisationId),
                    builder.Ascending(a => a.MaterialType),
                    builder.Ascending(a => a.Year)
                )
            ),
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.ApplicationReference),
                new CreateIndexOptions { Unique = true, Sparse = true }
            ),
            // Backs GetByCaseManagementWorkItemIdAsync, called on every inbound CM query push —
            // without this the lookup is a full collection scan (RA-311 OBE-2).
            new CreateIndexModel<AccreditationApplicationModel>(
                builder.Ascending(a => a.CaseManagementWorkItemId),
                new CreateIndexOptions { Unique = true, Sparse = true }
            ),
        ];
    }
}
