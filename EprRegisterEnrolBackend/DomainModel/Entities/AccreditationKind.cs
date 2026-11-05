namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// Which accreditation application this is. Explicit, because <see cref="AccreditationDetails.SourceAccreditationId"/>
/// is null both for a New application and for a migrated 2027 Reapply (register-enrol-application-types.md).
/// </summary>
public enum AccreditationKind
{
    New,
    Reapply,
}
