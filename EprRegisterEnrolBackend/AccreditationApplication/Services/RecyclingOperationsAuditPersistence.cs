using EprRegisterEnrolBackend.AccreditationApplication.Models;
using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

// RA-469 AC15/AC19: write-only audit trail for the recycling-operations PATCH endpoint. Entirely
// separate from AccreditationApplicationOverseasSites.Versions (the existing [JsonIgnore]
// snapshot list) - this class never references that model or its persistence, by design.
public class RecyclingOperationsAuditPersistence
    : MongoService<RecyclingOperationsAuditRecord>,
        IRecyclingOperationsAuditPersistence
{
    public RecyclingOperationsAuditPersistence(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, "recyclingOperationsAudit", loggerFactory)
    {
        // No custom index needed: this collection is write-only (no queries are made against it
        // from this backend, see IRecyclingOperationsAuditPersistence), so the default
        // built-in unique index on _id is sufficient. DefineIndexes returns [] below, so
        // EnsureIndexes short-circuits before any Mongo call and this constructor stays
        // network-safe to build (the property WebApplicationFactory-based tests that don't
        // override this interface rely on).
    }

    public async Task RecordAsync(
        RecyclingOperationsAuditRecord record,
        CancellationToken ct = default
    )
    {
        // Stamp the timestamp here, at persistence time, rather than trusting whatever value the
        // caller's record was constructed with - mirrors
        // AccreditationApplicationPersistence.UpdateAsync's `application.UpdatedAt =
        // DateTime.UtcNow` immediately before the write.
        record.Timestamp = DateTime.UtcNow;

        await Collection.InsertOneAsync(record, cancellationToken: ct);
    }

    protected override List<CreateIndexModel<RecyclingOperationsAuditRecord>> DefineIndexes(
        IndexKeysDefinitionBuilder<RecyclingOperationsAuditRecord> builder
    ) => [];
}
