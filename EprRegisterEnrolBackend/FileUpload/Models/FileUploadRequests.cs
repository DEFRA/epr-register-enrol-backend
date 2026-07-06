using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.FileUpload.Models;

public class CreateFileUploadRequest
{
    public required string OrganisationId { get; set; }
    public required MaterialType Material { get; set; }
    public required int YearOfAccreditation { get; set; }
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public required string ContentType { get; set; }
    public required string S3Key { get; set; }
    public string? S3Bucket { get; set; }
    public string? UploadedByUserId { get; set; }
    public FileScanStatus ScanStatus { get; set; } = FileScanStatus.Pending;
}
