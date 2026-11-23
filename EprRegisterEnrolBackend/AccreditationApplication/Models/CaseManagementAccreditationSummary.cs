namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

/// <summary>
/// RA-769: the response of the case management accreditation summary read: an explicit allow-list of what an
/// Approved application shows a caseworker. Shaped like the work item payload an application is submitted
/// with (lower-case material, glass process wire value), so the Case Management service reads it with the same code.
/// Never the persistence model, and never the S3 details of a file or the new-site markers.
/// </summary>
public sealed record CaseManagementAccreditationSummary(
    string? OrganisationName,
    int? OrganisationNumber,
    string? AccreditationNumber,
    string? RegistrationNumber,
    int AccreditationYear,
    string? WasteProcessingType,
    string Material,
    GlassRecyclingProcess? GlassRecyclingProcess,
    string? SiteAddress,
    string? CompanyRegisteredAddress
)
{
    public static CaseManagementAccreditationSummary From(AccreditationApplicationModel application) =>
        new(
            application.OrganisationName,
            application.OrgId,
            application.AccreditationReference,
            application.RegistrationReference,
            application.Year,
            application.WasteProcessingType,
            application.MaterialType.ToString().ToLowerInvariant(),
            application.GlassRecyclingProcess,
            application.SiteAddress,
            application.CompanyRegisteredAddress
        );
}
