namespace EprRegisterEnrolBackend.DomainModel.Entities;

public class Partner
{
    public string? Name { get; set; }

    /// <summary>ReEx's partner type: <c>company</c> or <c>individual</c>.</summary>
    public string? Type { get; set; }
}
