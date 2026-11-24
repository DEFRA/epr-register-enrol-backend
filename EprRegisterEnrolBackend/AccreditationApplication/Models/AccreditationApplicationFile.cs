namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationFile
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public required string ContentType { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public required string UploadedByUserId { get; set; }
    public FileScanStatus ScanStatus { get; set; } = FileScanStatus.Pending;
    public AccreditationFileDocumentType? DocumentType { get; set; }
    public required string S3Key { get; set; }
    public string? S3Bucket { get; set; }
}

// Stored as a number. The Admin UI (epr-register-enrol-admin, record.js) keeps a copy of
// these values in this order to show their names: change it there too.
public enum FileScanStatus
{
    Pending,
    Clean,
    Infected,
}

// Stored as a number. The Admin UI (epr-register-enrol-admin, record.js) keeps a copy of
// these values in this order to show their names: change it there too.
public enum AccreditationFileDocumentType
{
    SamplingPlan,
    SupportingEvidence,
}
