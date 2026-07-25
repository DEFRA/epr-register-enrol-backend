namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

public class AccreditationApplicationQuery
{
    public string? QueryNote { get; set; }

    public List<QuerySubmission> QuerySubmissions { get; set; } = [];

    // CM section keys from the most recent raise, still outstanding. Needed because editing a
    // queried section clears its own SectionStatus away from Queried immediately (on the very
    // next PATCH, not deferred to resubmit) — so by resubmit time, a touched section no longer
    // reads Queried at all and can't be identified from live section state alone. Overwritten on
    // every raise (mirrors QueryNote) and cleared once resubmitted.
    public List<string> QueriedSectionKeys { get; set; } = [];
}

public class QuerySubmission
{
    public DateTime QuerySubmissionTime { get; set; }

    public List<string> SectionKeys { get; set; } = [];

    public required QuerySubmitterContactDetails QuerySubmitterContactDetails { get; set; }
}

public class QuerySubmitterContactDetails
{
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public required string Role { get; set; }
}
