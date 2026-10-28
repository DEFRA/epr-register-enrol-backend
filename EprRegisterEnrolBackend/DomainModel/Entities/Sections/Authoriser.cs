namespace EprRegisterEnrolBackend.DomainModel.Entities.Sections;

/// <summary>A person with authority to issue PRNs (legacy <c>PrnsAuthoriser</c>).</summary>
public class Authoriser
{
    public required string FullName { get; set; }
    public required string Email { get; set; }

    /// <summary>Introduced during this application, so the regulator can spot it (RA-292 AC03). Server-derived.</summary>
    public bool IsNew { get; set; }
}
