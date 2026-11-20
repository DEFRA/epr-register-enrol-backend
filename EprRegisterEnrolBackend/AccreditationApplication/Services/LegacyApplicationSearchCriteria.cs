using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>A field the admin search's text can be matched against (RA-740).</summary>
public enum LegacyApplicationSearchField
{
    /// <summary><see cref="AccreditationApplicationModel.AccreditationReference"/>.</summary>
    AccreditationNumber,

    /// <summary><see cref="AccreditationApplicationModel.RegistrationReference"/>.</summary>
    RegistrationNumber,

    /// <summary><see cref="AccreditationApplicationModel.OrgId"/>, matched as text.</summary>
    OrganisationNumber,

    /// <summary><see cref="AccreditationApplicationModel.OrganisationName"/>.</summary>
    OrganisationName,
}

/// <summary>
/// RA-740: what the admin API narrows the Legacy applications by. <see cref="Text"/> is matched as
/// literal text, ignoring case, anywhere in any of <see cref="Fields"/> (OR); an empty
/// <see cref="Fields"/> means all of them. <see cref="Statuses"/> combine with OR. The text, the
/// statuses and the year combine with AND. Every member is optional.
/// </summary>
public sealed record LegacyApplicationSearchCriteria(
    string? Text = null,
    IReadOnlyCollection<LegacyApplicationSearchField>? Fields = null,
    IReadOnlyCollection<ApplicationStatus>? Statuses = null,
    int? Year = null
);