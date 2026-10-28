namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>A person ReEx lists against the organisation, with the roles ReEx gives them.</summary>
public class OrganisationUser
{
    public string? ContactId { get; set; }
    public string? FullName { get; set; }
    public string? Email { get; set; }
    public List<string> Roles { get; set; } = [];
}
