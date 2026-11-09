namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>One uploaded BES evidence file (legacy <c>BesEvidenceFileModel</c>).</summary>
public class BesEvidenceFile
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public string? ContentType { get; set; }
    public string? ScanStatus { get; set; }
    public string? BesEvidenceValidFromDate { get; set; }
    public string? BesEvidenceExpiryDate { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
    public required string S3Key { get; set; }
    public string? S3Bucket { get; set; }
}
