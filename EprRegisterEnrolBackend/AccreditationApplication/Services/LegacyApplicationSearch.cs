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
public sealed record LegacyApplicationSearch(
    string? Text = null,
    IReadOnlyCollection<LegacyApplicationSearchField>? Fields = null,
    IReadOnlyCollection<ApplicationStatus>? Statuses = null,
    int? Year = null
);

/// <summary>
/// RA-740: searches every application in the Legacy <c>accreditationApplications</c> collection,
/// whatever its status or year, for the admin API. Always the Legacy collection, even once the
/// rest of the service reads the domain model, until RA-677 retires or repoints it.
/// </summary>
public interface ILegacyApplicationSearch
{
    /// <summary>
    /// A page of the applications matching <paramref name="search"/>, most recently updated first
    /// (<c>UpdatedAt</c> descending, then <c>_id</c> descending; an application updated between pages moves), and the total
    /// across all pages. Read-only.
    /// </summary>
    Task<(IReadOnlyList<AccreditationApplicationModel> Items, long Total)> SearchLegacyApplicationsAsync(
        LegacyApplicationSearch search,
        long skip,
        int limit,
        TimeSpan? maxTime = null,
        CancellationToken cancellationToken = default
    );
}
