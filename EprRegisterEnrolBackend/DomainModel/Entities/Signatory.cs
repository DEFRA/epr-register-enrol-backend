namespace EprRegisterEnrolBackend.DomainModel.Entities;

public class Signatory
{
    public required string FullName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
}
