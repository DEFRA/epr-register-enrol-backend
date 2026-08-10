using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// Derives <see cref="PrnsAuthoriser.IsNew"/> for the authority-to-issue contacts (RA-292 AC03).
///
/// Newness is a regulator-facing concept and is owned entirely by the server: the operator never
/// sees or sets it, and a value arriving on a request body is advisory only. Every write of the
/// PRNs section re-derives it by comparing the incoming authorisers against the ones already
/// persisted, so a client that echoes the field, drops it, or fabricates it all produce the same
/// stored result. In particular a client cannot downgrade a server-derived <c>true</c>.
/// </summary>
public static class PrnsAuthoriserMerge
{
    /// <summary>
    /// Returns the incoming authorisers with <see cref="PrnsAuthoriser.IsNew"/> derived by
    /// matching each one's email against <paramref name="persisted"/> — trimmed and compared
    /// case-insensitively. An email that was not persisted before is new; one that was keeps
    /// whatever value the server previously derived for it.
    ///
    /// The incoming list replaces the persisted one wholesale: an authoriser the client omitted
    /// is genuinely dropped, never resurrected from <paramref name="persisted"/>.
    /// </summary>
    public static List<PrnsAuthoriser> Merge(
        IEnumerable<PrnsAuthoriser>? persisted,
        IEnumerable<PrnsAuthoriser>? incoming
    )
    {
        if (incoming is null)
            return [];

        var wasPersistedAsNew = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var authoriser in persisted ?? [])
        {
            // First entry wins if the persisted list somehow holds the same email twice.
            wasPersistedAsNew.TryAdd(NormaliseEmail(authoriser.Email), authoriser.IsNew);
        }

        return
        [
            .. incoming.Select(authoriser => new PrnsAuthoriser
            {
                FullName = authoriser.FullName,
                Email = authoriser.Email,
                IsNew =
                    !wasPersistedAsNew.TryGetValue(
                        NormaliseEmail(authoriser.Email),
                        out var previouslyNew
                    ) || previouslyNew,
            }),
        ];
    }

    /// <summary>
    /// Stamps authorisers as not new. Used when carrying prior-year contacts across on Seed —
    /// they existed before this application, so the regulator must never see them flagged.
    /// </summary>
    public static List<PrnsAuthoriser> MarkAsExisting(IEnumerable<PrnsAuthoriser>? authorisers) =>
        [
            .. (authorisers ?? []).Select(authoriser => new PrnsAuthoriser
            {
                FullName = authoriser.FullName,
                Email = authoriser.Email,
                IsNew = false,
            }),
        ];

    private static string NormaliseEmail(string? email) => email?.Trim() ?? string.Empty;
}
