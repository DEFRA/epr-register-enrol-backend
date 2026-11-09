namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>Who submitted the application (legacy <c>SubmittedByModel</c>).</summary>
public class Submitter
{
    public required string FullName { get; set; }
    public required string JobTitle { get; set; }
    public string? Email { get; set; }
}
