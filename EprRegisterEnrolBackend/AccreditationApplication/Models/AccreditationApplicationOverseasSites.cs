namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationOverseasSites
{
    public List<OverseasSiteModel> Sites { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;
}

public class OverseasSiteModel
{
    public required int SiteId { get; set; }
    public string? OrsId { get; set; }
    public required string SiteName { get; set; }
    public string? SiteAddress { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? Country { get; set; }
    public string? Coordinates { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? OperationCode { get; set; }
    public string? Code1 { get; set; }
    public string? Code2 { get; set; }
    public string? Code3 { get; set; }
    public string? RepatriatedLoads { get; set; }
    public bool? ConditionsOfExport { get; set; }
    public bool IsEu { get; set; }
    public bool IsOecd { get; set; }
    public bool Selected { get; set; } = true;
    public BesEvidenceModel? BesEvidence { get; set; }
}

public class BesEvidenceModel
{
    public List<BesEvidenceFileModel> BesEvidenceUploads { get; set; } = [];
    public bool DoYouWantToUploadMoreEvidence { get; set; } = true;
}

public class BesEvidenceFileModel
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public string? ContentType { get; set; }
    public string? ScanStatus { get; set; }
    public string? BesEvidenceValidFromDate { get; set; }
    public string? BesEvidenceExpiryDate { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string? UploadedBy { get; set; }
    public required string S3Key { get; set; }
    public string? S3Bucket { get; set; }
}

public class AccreditationApplicationBesEvidence
{
    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;
}
