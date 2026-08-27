namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class SeedRequest
{
    public required int Year { get; set; }
}

public class PatchPrnsRequest
{
    public PlannedTonnageBand? PlannedTonnageBand { get; set; }
    public List<PrnsAuthoriser>? Authorisers { get; set; }

    /// <summary>RA-496: operator's save intent — InProgress ("save and come back later") or
    /// Completed ("save and continue"). Null falls back to the legacy auto-computed status.</summary>
    public SectionStatus? SectionStatus { get; set; }
}

public class PatchTonnageRequest
{
    public PlannedTonnageBand? PlannedTonnageBand { get; set; }
    public List<PrnsAuthoriser>? Authorisers { get; set; }

    /// <summary>RA-496: operator's save intent — InProgress ("save and come back later") or
    /// Completed ("save and continue"). Null falls back to the legacy auto-computed status.</summary>
    public SectionStatus? SectionStatus { get; set; }
}

public class PatchBusinessPlanRequest
{
    public int? NewInfrastructurePercent { get; set; }
    public int? PriceSupportPercent { get; set; }
    public int? BusinessCollectionsPercent { get; set; }
    public int? CommunicationsPercent { get; set; }
    public int? NewMarketsPercent { get; set; }
    public int? NewUsesPercent { get; set; }
    public int? OtherPercent { get; set; }

    public string? NewInfrastructureDetail { get; set; }
    public string? PriceSupportDetail { get; set; }
    public string? BusinessCollectionsDetail { get; set; }
    public string? CommunicationsDetail { get; set; }
    public string? NewMarketsDetail { get; set; }
    public string? NewUsesDetail { get; set; }
    public string? OtherDetail { get; set; }

    /// <summary>When true, bypasses the sum-to-100 check (used by "Save and come back later").</summary>
    public bool IsPartialSave { get; set; }

    /// <summary>RA-496: operator's save intent — InProgress ("save and come back later") or
    /// Completed ("save and continue"). Null falls back to the legacy auto-computed status.</summary>
    public SectionStatus? SectionStatus { get; set; }
}

public class PatchSamplingPlanRequest
{
    public List<AccreditationApplicationFile>? Files { get; set; }

    /// <summary>RA-496: operator's save intent — InProgress ("save and come back later") or
    /// Completed ("save and continue"). Null falls back to the legacy auto-computed status.</summary>
    public SectionStatus? SectionStatus { get; set; }
}

public class PatchOverseasSitesRequest
{
    public List<OverseasSiteModel>? Sites { get; set; }

    /// <summary>RA-496: operator's save intent — InProgress ("save and come back later") or
    /// Completed ("save and continue"). Null falls back to the legacy binary auto-computed
    /// status (this section has no partial-completion concept beyond that).</summary>
    public SectionStatus? SectionStatus { get; set; }
}

// RA-482: OrsId is deliberately absent -- the server now generates it authoritatively
// (see OrsIdGenerator / AddOverseasSite), closing the race condition inherent in trusting
// a client-computed value.
public record AddOverseasSiteRequest
{
    public required string SiteName { get; set; }
    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string TownOrCity { get; set; }
    public required string Country { get; set; }
    public string? Coordinates { get; set; }
    public required string ContactName { get; set; }
    public required string ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public required List<string> OperationCodes { get; set; }
    public required string Code1 { get; set; }
    public string? Code2 { get; set; }
    public string? Code3 { get; set; }
    public required string RepatriatedLoads { get; set; }
    public bool? ConditionsOfExport { get; set; }
}

public record PromoteOverseasSiteRequest
{
    public required string SiteName { get; set; }
    public required string AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public required string TownOrCity { get; set; }
    public required string Country { get; set; }
    public string? Coordinates { get; set; }
    public required string ContactName { get; set; }
    public required string ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public required List<string> OperationCodes { get; set; }
    public required string Code1 { get; set; }
    public string? Code2 { get; set; }
    public string? Code3 { get; set; }
    public required string RepatriatedLoads { get; set; }
    public bool? ConditionsOfExport { get; set; }
}

public record AddInterimSiteRequest
{
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
}

// FileId/Filename/ContentType/ScanStatus/S3Key/S3Bucket are deliberately NOT here:
// they must come from the server-held PendingUploadService record for FileUploadId
// (populated only by the real CDP-uploader webhook callback), never from the client
// directly — see H6 in the 2026-08-08 pentest report.
public class AddBesEvidenceFileRequest
{
    public required string FileUploadId { get; set; }
    public string? BesEvidenceValidFromDate { get; set; }
    public string? BesEvidenceExpiryDate { get; set; }
}

public class PatchBesEvidenceRequest
{
    public bool? DoYouWantToUploadMoreEvidence { get; set; }
}

// RA-469: regulator-scoped correction of an ORS's recycling operation codes only - no other
// overseas-site fields are editable through this endpoint.
public class PatchRecyclingOperationsRequest
{
    public required List<string> OperationCodes { get; set; }
}

public class PatchBesEvidenceSectionRequest
{
    public SectionStatus? SectionStatus { get; set; }
}

public class SubmitRequest
{
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public string? Email { get; set; }

    // RA-503: the operator's nation-specific bank payment reference, computed by
    // buildPaymentReference in epr-register-enrol-frontend and forwarded so management-be
    // can show the regulator the same reference the operator was shown. Optional so a caller
    // that predates this (or doesn't compute one) doesn't fail submission.
    public string? PaymentReference { get; set; }
}

// Pushed by ManagementBe when CM raises a query (RA-311 §3/§5/§6). SectionKeys use CM's own
// closed six-key vocabulary, not operator section names — see AccreditationApplicationSections.
public class QueryFromCaseManagementRequest
{
    public string? QueryNote { get; set; }
    public List<string> SectionKeys { get; set; } = [];
}

// Pushed by ManagementBe on every generic work-item transition (RA-368) — the OJ-facing
// projection of CM's progress. ToStateId is CM's raw, stable wire-contract state id.
public class StatusChangedFromCaseManagementRequest
{
    public required string ToStateId { get; set; }
    public string? ToStateDisplayName { get; set; }
    public required string ActionId { get; set; }
    public string? ActionDisplayName { get; set; }
    public DateTime OccurredAt { get; set; }
}

// RA-448: shared by both GenerateOrUpdateRegistrationNumber and
// GenerateOrUpdateAccreditationNumber. Nation, OrgId and Year are all
// caller-supplied rather than derived/assumed internally - this backend has
// no reliable real organisation data source for Nation/OrgId (see the
// RA-448 design notes), and per explicit product direction (2026-08-19) no
// assumption about the year (calendar year at generation time or otherwise)
// may be made either - the caller must always pass it. The one exception is
// accreditation's "reapply" regenerate, which never takes a Year at all -
// it increments whatever YY the existing number already holds, with no
// calendar dependency. Regenerate is ignored (treated as false) on a
// first-ever generate; it only changes behaviour when a number already
// exists (AC5).
//
// Nation is a raw string, not the Nation enum, deliberately: Minimal API's
// default JSON body binding throws (surfacing as an unhandled 500, not a
// clean 400) when an enum-typed property can't parse the supplied string -
// that would fail AC6's "unknown/invalid ... returns 400" requirement before
// the request ever reaches the validator. Parsed explicitly after validation.
public class GenerateOrUpdateRegulatoryNumberRequest
{
    public string? Nation { get; set; }
    public int? OrgId { get; set; }
    public int? Year { get; set; }
    public bool Regenerate { get; set; }
}

// Submitter contact details captured on the query-declaration page. No completeness validation —
// explicit in the ticket (RA-311 §6).
public class ResubmitRequest
{
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public string? Role { get; set; }
}

// RA-252: the operator's stated reason for withdrawing, captured on the frontend's
// withdraw-application confirmation page. FullName/Email identify the acting user (who is
// withdrawing now), not the application's original submitter — mirrors ResubmitRequest.
public class WithdrawRequest
{
    public required string Reason { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
}

public record SubmitResponse
{
    public string? AccreditationReference { get; init; }

    /// <summary>
    /// Reference assigned by the case management service (RA-XXXXXXXXX).
    /// Null if the case management backend did not return a reference (e.g. stub mode).
    /// </summary>
    public string? CaseManagementReference { get; init; }
}

// FileId/Filename/ContentType/ScanStatus/S3Key/S3Bucket are deliberately NOT here:
// they must come from the server-held PendingUploadService record for FileUploadId
// (populated only by the real CDP-uploader webhook callback), never from the client
// directly — see H6 in the 2026-08-08 pentest report.
public class FileUploadRequest
{
    public required string FileUploadId { get; set; }
    public AccreditationFileDocumentType? DocumentType { get; set; }
}
