using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>
/// An uploaded file, as the domain model stores it: the legacy <see cref="AccreditationApplicationFile"/> with its
/// enums stored as strings, which every new collection does (§3.2).
/// </summary>
public class ApplicationFile
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public required string ContentType { get; set; }
    public DateTime UploadedAt { get; set; }
    public required string UploadedByUserId { get; set; }

    [BsonRepresentation(BsonType.String)]
    public FileScanStatus ScanStatus { get; set; } = FileScanStatus.Pending;

    [BsonRepresentation(BsonType.String)]
    public AccreditationFileDocumentType? DocumentType { get; set; }

    public required string S3Key { get; set; }
    public string? S3Bucket { get; set; }
}
