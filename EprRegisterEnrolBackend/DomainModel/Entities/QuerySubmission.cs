namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>One response to a query: which sections were resubmitted, when and by whom.</summary>
public class QuerySubmission
{
    public DateTime QuerySubmissionTime { get; set; }
    public List<string> SectionKeys { get; set; } = [];
    public required QuerySubmitter QuerySubmitterContactDetails { get; set; }
}
