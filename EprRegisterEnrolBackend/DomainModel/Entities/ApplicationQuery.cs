namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>The regulator's query on an application and the operator's responses (legacy <c>AccreditationApplicationQuery</c>).</summary>
public class ApplicationQuery
{
    public string? QueryNote { get; set; }
    public List<QuerySubmission> QuerySubmissions { get; set; } = [];
    public List<string> QueriedSectionKeys { get; set; } = [];
}
