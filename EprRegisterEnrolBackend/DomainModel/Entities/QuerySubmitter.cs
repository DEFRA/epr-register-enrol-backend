namespace EprRegisterEnrolBackend.DomainModel.Entities;

public class QuerySubmitter
{
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public required string Role { get; set; }
}
