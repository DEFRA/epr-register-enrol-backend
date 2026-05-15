using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.FileUpload.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.FileUpload.Services;

public class FileUploadPersistence(
    IMongoDbClientFactory connectionFactory,
    ILoggerFactory loggerFactory)
    : MongoService<FileUploadModel>(connectionFactory, "fileUploads", loggerFactory),
        IFileUploadPersistence
{
    public async Task<FileUploadModel?> CreateAsync(FileUploadModel fileUpload)
    {
        try
        {
            await Collection.InsertOneAsync(fileUpload);
            return fileUpload;
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to insert file upload for org={OrganisationId}", fileUpload.OrganisationId);
            return null;
        }
    }

    public async Task<IEnumerable<FileUploadModel>> GetByOrganisationAsync(string organisationId)
    {
        return await Collection
            .Find(f => f.OrganisationId == organisationId)
            .SortByDescending(f => f.UploadedAt)
            .ToListAsync();
    }

    public async Task<FileUploadModel?> GetByIdAsync(string fileUploadId)
    {
        if (!ObjectId.TryParse(fileUploadId, out var objectId))
            return null;

        return await Collection
            .Find(f => f.Id == objectId)
            .FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<FileUploadModel>> GetByOrganisationMaterialAndYearAsync(
        string organisationId, MaterialType material, int year)
    {
        return await Collection
            .Find(f => f.OrganisationId == organisationId
                       && f.Material == material
                       && f.YearOfAccreditation == year)
            .SortByDescending(f => f.UploadedAt)
            .ToListAsync();
    }

    protected override List<CreateIndexModel<FileUploadModel>> DefineIndexes(
        IndexKeysDefinitionBuilder<FileUploadModel> builder)
    {
        return
        [
            new CreateIndexModel<FileUploadModel>(builder.Ascending(f => f.OrganisationId)),
            new CreateIndexModel<FileUploadModel>(builder.Ascending(f => f.Material)),
            new CreateIndexModel<FileUploadModel>(builder.Ascending(f => f.YearOfAccreditation)),
            new CreateIndexModel<FileUploadModel>(builder.Ascending(f => f.ScanStatus)),
            new CreateIndexModel<FileUploadModel>(
                builder.Combine(
                    builder.Ascending(f => f.OrganisationId),
                    builder.Ascending(f => f.Material),
                    builder.Ascending(f => f.YearOfAccreditation)))
        ];
    }
}
