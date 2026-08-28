using EprRegisterEnrolBackend.Utils.Mongo;
using MongoDB.Driver;

namespace EprRegisterEnrolBackend.Auth;

// Mongo-backed replacement for the old IMemoryCache + static-lock nonce store
// (epr-register-enrol-backend-0i1): an insert against the collection's unique
// _id index is atomic, so a duplicate-key exception on insert *is* "nonce
// already used" - no separate lock is needed, and replay protection is now
// shared across every running instance instead of being per-process.
public class CaseManagementAuthNonceStore
    : MongoService<CaseManagementAuthNonceDocument>,
        ICaseManagementAuthNonceStore
{
    public CaseManagementAuthNonceStore(
        IMongoDbClientFactory connectionFactory,
        ILoggerFactory loggerFactory
    )
        : base(connectionFactory, "caseManagementAuthNonces", loggerFactory) { }

    public async Task<bool> TryConsumeAsync(
        string nonce,
        TimeSpan ttl,
        CancellationToken ct = default
    )
    {
        var document = new CaseManagementAuthNonceDocument
        {
            Id = nonce,
            ExpiresAt = DateTime.UtcNow.Add(ttl),
        };

        try
        {
            await Collection.InsertOneAsync(document, cancellationToken: ct);
            return true;
        }
        catch (MongoWriteException ex)
            when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    protected override List<CreateIndexModel<CaseManagementAuthNonceDocument>> DefineIndexes(
        IndexKeysDefinitionBuilder<CaseManagementAuthNonceDocument> builder
    ) =>
        [
            new CreateIndexModel<CaseManagementAuthNonceDocument>(
                builder.Ascending(n => n.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }
            ),
        ];
}
