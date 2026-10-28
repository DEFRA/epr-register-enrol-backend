namespace EprRegisterEnrolBackend.DomainModel.Diagnostics;

/// <summary>
/// Aggregate counts only: no names, addresses, contact details or free text, so a report is safe to log and to hand
/// to someone without production data access. Each count is of documents (or groups) that have the shape.
/// </summary>
public record LegacyDataSurveyReport(
    long Applications,
    long Int32EnumDocuments,
    long InterimSiteSingularOnlyDocuments,
    long NullOrsIdDocuments,
    long LegacySnInterimNumberDocuments,
    long DuplicateLiveApplicationGroups,
    long ConflictingRegistrationNumberGroups,
    long ApprovedWithoutAccreditationReference,
    long CaseManagementReferencePresent,
    long DuplicateAccreditationReferenceGroups
)
{
    public string ToLogLine() =>
        $"applications={Applications} int32Enums={Int32EnumDocuments} "
        + $"interimSiteSingularOnly={InterimSiteSingularOnlyDocuments} nullOrsIds={NullOrsIdDocuments} "
        + $"legacySnInterimNumbers={LegacySnInterimNumberDocuments} "
        + $"duplicateLiveApplicationGroups={DuplicateLiveApplicationGroups} "
        + $"conflictingRegistrationNumberGroups={ConflictingRegistrationNumberGroups} "
        + $"approvedWithoutAccreditationReference={ApprovedWithoutAccreditationReference} "
        + $"caseManagementReferencePresent={CaseManagementReferencePresent} "
        + $"duplicateAccreditationReferenceGroups={DuplicateAccreditationReferenceGroups}";
}
