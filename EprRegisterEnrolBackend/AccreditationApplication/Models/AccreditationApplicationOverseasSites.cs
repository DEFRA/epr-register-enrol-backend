namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationOverseasSites
{
    public List<OverseasSiteModel> Sites { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;
}

public class OverseasSiteModel
{
    public required int SiteId { get; set; }
    public required string SiteName { get; set; }
    public string? SiteAddress { get; set; }
    public string? Country { get; set; }
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
}

public class AccreditationApplicationBesEvidence
{
    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;
}
