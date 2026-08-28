using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.CdpUploader.Models;

/// <summary>
/// Mongo-persisted counterpart of the state <c>PendingUploadService</c> used to
/// hold in a <c>ConcurrentDictionary</c>. Id is the CDP <c>fileUploadId</c> the
/// caller supplies, never Mongo-generated - mirrors
/// <c>RegulatoryNumberSequenceCounter</c>'s explicit-key pattern.
/// </summary>
public class PendingUploadDocument
{
    [BsonId]
    public required string Id { get; set; }

    public required string CdpStatusUrl { get; set; }
    public string? CdpUploadId { get; set; }
    public string? S3Bucket { get; set; }
    public string? S3Path { get; set; }
    public CdpCallbackFile? ScanResult { get; set; }
    public FileProcessingStatus Status { get; set; }

    /// <summary>
    /// TTL index target (see PendingUploadService.DefineIndexes) - cleans up
    /// abandoned uploads automatically rather than growing the collection
    /// forever. Not a business rule: real CDP scans resolve in seconds/minutes.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}
