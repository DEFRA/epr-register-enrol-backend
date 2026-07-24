namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationFile
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public required string ContentType { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public required string UploadedByUserId { get; set; }
    public FileScanStatus ScanStatus { get; set; } = FileScanStatus.Pending;
    public required string S3Key { get; set; }
    public string? S3Bucket { get; set; }
}

public enum FileScanStatus
{
    Pending,
    Clean,
    Infected
}
