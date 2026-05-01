namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class SeedRequest
{
    public required MaterialType MaterialType { get; set; }
    public required int Year { get; set; }
    public string? SiteId { get; set; }
}

public class PatchPrnsRequest
{
    public PlannedTonnageBand? PlannedTonnageBand { get; set; }
    public List<PrnsAuthoriser>? Authorisers { get; set; }
}

public class PatchBusinessPlanRequest
{
    public int? NewInfrastructurePercent { get; set; }
    public int? PriceSupportPercent { get; set; }
    public int? BusinessCollectionsPercent { get; set; }
    public int? CommunicationsPercent { get; set; }
    public int? NewMarketsPercent { get; set; }
    public int? NewUsesPercent { get; set; }

    public string? NewInfrastructureDetail { get; set; }
    public string? PriceSupportDetail { get; set; }
    public string? BusinessCollectionsDetail { get; set; }
    public string? CommunicationsDetail { get; set; }
    public string? NewMarketsDetail { get; set; }
    public string? NewUsesDetail { get; set; }

    /// <summary>When true, bypasses the sum-to-100 check (used by "Save and come back later").</summary>
    public bool IsPartialSave { get; set; }
}

public class PatchSamplingPlanRequest
{
    public List<AccreditationApplicationFile>? Files { get; set; }
}

public class SubmitRequest
{
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public required string Email { get; set; }
}

public class FileUploadRequest
{
    public required string FileId { get; set; }
    public required string Filename { get; set; }
    public required string ContentType { get; set; }
}
