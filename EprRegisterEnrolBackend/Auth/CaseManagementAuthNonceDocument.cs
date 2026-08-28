using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.Auth;

/// <summary>
/// One document per consumed nonce - Id is the nonce value itself, so the
/// collection's mandatory unique index on _id is what makes
/// <see cref="CaseManagementAuthNonceStore.TryConsumeAsync"/> atomic across
/// concurrent requests and multiple running instances (epr-register-enrol-backend-0i1).
/// </summary>
public class CaseManagementAuthNonceDocument
{
    [BsonId]
    public required string Id { get; set; }

    /// <summary>TTL index target - matches CaseManagementAuthenticationHandler.ClockSkew.</summary>
    public DateTime ExpiresAt { get; set; }
}
