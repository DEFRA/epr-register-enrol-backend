using System.Text.Json.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.FileUpload.Models;

public class FileUploadModel
{
    [BsonId(IdGenerator = typeof(ObjectIdGenerator))]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Always)]
    public ObjectId? Id { get; set; }

    [BsonIgnore]
    public string? FileUploadId => Id?.ToString();

    public required string OrganisationId { get; set; }

    public required MaterialType Material { get; set; }

    public required int YearOfAccreditation { get; set; }

    public required string FileId { get; set; }

    public required string Filename { get; set; }

    public required string ContentType { get; set; }

    public required string S3Key { get; set; }

    public string? S3Bucket { get; set; }

    public FileScanStatus ScanStatus { get; set; } = FileScanStatus.Pending;

    public string? UploadedByUserId { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
