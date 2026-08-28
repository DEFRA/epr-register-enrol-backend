using EprRegisterEnrolBackend.CdpUploader.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.CdpUploader.Services;

// Mongo-backed replacement for the old ConcurrentDictionary singleton
// (epr-register-enrol-backend-6y2): state now survives a restart and is shared
// correctly across multiple running instances, which the in-memory version
// could not provide.
public class PendingUploadService : MongoService<PendingUploadDocument>, IPendingUploadService
{
    // Cleanup window for abandoned uploads, not a business rule - real CDP scans
    // resolve in seconds/minutes. Generous enough that a slow scan never loses
    // its pending record out from under it.
    private static readonly TimeSpan DocumentTtl = TimeSpan.FromHours(24);

    public PendingUploadService(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, "pendingUploads", loggerFactory) { }

    public async Task CreateAsync(
        string fileUploadId,
        string cdpStatusUrl,
        string? cdpUploadId = null,
        string? s3Bucket = null,
        string? s3Path = null,
        CancellationToken ct = default
    )
    {
        var document = new PendingUploadDocument
        {
            Id = fileUploadId,
            CdpStatusUrl = cdpStatusUrl,
            CdpUploadId = cdpUploadId,
            S3Bucket = s3Bucket,
            S3Path = s3Path,
            Status = FileProcessingStatus.Preprocessing,
            ExpiresAt = DateTime.UtcNow.Add(DocumentTtl),
        };

        await Collection.ReplaceOneAsync(
            Builders<PendingUploadDocument>.Filter.Eq(u => u.Id, fileUploadId),
            document,
            new ReplaceOptions { IsUpsert = true },
            ct
        );

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "Upload {FileUploadId} state → {Status}",
                fileUploadId,
                FileProcessingStatus.Preprocessing
            );
        }
    }

    public async Task<PendingUploadDetails?> TryGetPendingUploadDetailsAsync(
        string fileUploadId,
        CancellationToken ct = default
    )
    {
        var upload = await Collection.Find(u => u.Id == fileUploadId).FirstOrDefaultAsync(ct);
        if (upload is null || upload.Status != FileProcessingStatus.Preprocessing)
            return null;

        return new PendingUploadDetails(
            upload.CdpStatusUrl,
            upload.CdpUploadId,
            upload.S3Bucket,
            upload.S3Path
        );
    }

    public async Task CompleteAsync(
        string fileUploadId,
        CdpCallbackFile fileResult,
        CancellationToken ct = default
    )
    {
        var newStatus =
            fileResult.FileStatus == "complete"
                ? FileProcessingStatus.Validated
                : FileProcessingStatus.Rejected;

        // Set(...) always applies; SetOnInsert(...) only takes effect on the insert
        // branch, mirroring the old AddOrUpdate's two branches - an update leaves
        // CdpStatusUrl/CdpUploadId/S3Bucket/S3Path/ExpiresAt from the original
        // Create untouched, an insert (a callback that raced ahead of Create)
        // gets the same empty-string CdpStatusUrl the old code used.
        var filter = Builders<PendingUploadDocument>.Filter.Eq(u => u.Id, fileUploadId);
        var update = Builders<PendingUploadDocument>
            .Update.Set(u => u.ScanResult, fileResult)
            .Set(u => u.Status, newStatus)
            .SetOnInsert(u => u.CdpStatusUrl, string.Empty)
            .SetOnInsert(u => u.ExpiresAt, DateTime.UtcNow.Add(DocumentTtl));

        await Collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);

        if (Logger.IsEnabled(LogLevel.Information))
        {
            Logger.LogInformation(
                "Upload {FileUploadId} state → {Status} (fileStatus={FileStatus})",
                fileUploadId,
                newStatus,
                fileResult.FileStatus
            );
        }
    }

    public async Task<IReadOnlyList<string>> GetPendingUploadIdsAsync(
        CancellationToken ct = default
    )
    {
        var filter = Builders<PendingUploadDocument>.Filter.Eq(
            u => u.Status,
            FileProcessingStatus.Preprocessing
        );

        return await Collection.Find(filter).Project(u => u.Id).ToListAsync(ct);
    }

    public async Task<CdpStatusResponse> GetStatusAsync(
        string fileUploadId,
        CancellationToken ct = default
    )
    {
        var upload = await Collection.Find(u => u.Id == fileUploadId).FirstOrDefaultAsync(ct);
        if (upload is null)
        {
            return new CdpStatusResponse
            {
                UploadStatus = "pending",
                ProcessingStatus = "preprocessing",
            };
        }

        return upload.Status switch
        {
            FileProcessingStatus.Preprocessing => new CdpStatusResponse
            {
                UploadStatus = "pending",
                ProcessingStatus = "preprocessing",
            },
            FileProcessingStatus.Validated => new CdpStatusResponse
            {
                UploadStatus = "ready",
                ProcessingStatus = "validated",
                Form = new CdpCallbackForm { File = upload.ScanResult },
            },
            FileProcessingStatus.Rejected => new CdpStatusResponse
            {
                UploadStatus = "ready",
                ProcessingStatus = "rejected",
                Form = new CdpCallbackForm { File = upload.ScanResult },
            },
            _ => new CdpStatusResponse
            {
                UploadStatus = "pending",
                ProcessingStatus = "preprocessing",
            },
        };
    }

    protected override List<CreateIndexModel<PendingUploadDocument>> DefineIndexes(
        IndexKeysDefinitionBuilder<PendingUploadDocument> builder
    ) =>
        [
            new CreateIndexModel<PendingUploadDocument>(
                builder.Ascending(u => u.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }
            ),
            new CreateIndexModel<PendingUploadDocument>(builder.Ascending(u => u.Status)),
        ];
}
