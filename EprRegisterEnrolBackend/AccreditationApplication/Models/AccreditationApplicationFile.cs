namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationFile
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public required string ContentType { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public required string UploadedByUserId { get; set; }
    public FileScanStatus ScanStatus { get; set; } = FileScanStatus.Pending;
}

public enum FileScanStatus
{
    Pending,
    Clean,
    Infected
}
