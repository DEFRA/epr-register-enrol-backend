using System.Text.Json.Serialization;

namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationOverseasSites
{
    public List<OverseasSiteModel> Sites { get; set; } = [];

    public SectionStatus SectionStatus { get; set; } = SectionStatus.NotStarted;

    // Snapshotted on Submit and on each resubmit-after-query — never sent to the FE (RA-311 §6).
    [JsonIgnore]
    public List<OverseasSitesSnapshot> Versions { get; set; } = [];
}

public class OverseasSitesSnapshot
{
    public List<OverseasSiteModel> Sites { get; set; } = [];
    public DateTime VersionedAt { get; set; }
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
    public bool IsNewSite { get; set; } = true;
    public bool RegisteredNowAccredited { get; set; } = false;

    // Undo stack for promote/revert (RA-298/RA-300): promoting a registered site pushes its
    // pre-promotion field values here before overwriting them; reverting pops the last entry
    // back over the current fields. Backend-internal only — never sent to the FE.
    [JsonIgnore]
    public List<OverseasSiteModel> PreviousSites { get; set; } = [];
    public InterimSiteModel? InterimSite { get; set; }
}

public class InterimSiteModel
{
    public required int SiteId { get; set; }
    public required string SiteNumber { get; set; }
    public required string Country { get; set; }
    public required string SiteName { get; set; }
    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string TownOrCity { get; set; }
    public string? StateOrRegion { get; set; }
    public string? Postcode { get; set; }
    public required string ContactName { get; set; }
    public required string ContactEmail { get; set; }
    public required string ContactPhone { get; set; }
    public bool IsNewSite { get; set; } = true;
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

    // Snapshotted on Submit and on each resubmit-after-query — never sent to the FE (RA-311 §6).
    [JsonIgnore]
    public List<BesEvidenceSnapshot> Versions { get; set; } = [];
}

public class BesEvidenceSnapshot
{
    public DateTime VersionedAt { get; set; }
}
