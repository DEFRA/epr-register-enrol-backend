using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-740: searches every application in the Legacy <c>accreditationApplications</c> collection,
/// whatever its status or year, for the admin API. Always the Legacy collection, even once the
/// rest of the service reads the domain model, until RA-677 retires or repoints it.
/// </summary>
public interface ILegacyApplicationSearch
{
    /// <summary>
    /// A page of the applications matching <paramref name="search"/>, most recently updated first
    /// (<c>UpdatedAt</c> descending, then <c>_id</c> descending; an application updated between
    /// pages moves), and the total across all pages. Read-only.
    /// </summary>
    Task<(IReadOnlyList<AccreditationApplicationModel> Items, long Total)> SearchLegacyApplicationsAsync(
        LegacyApplicationSearchCriteria search,
        long skip,
        int limit,
        TimeSpan? maxTime = null,
        CancellationToken cancellationToken = default
    );
}