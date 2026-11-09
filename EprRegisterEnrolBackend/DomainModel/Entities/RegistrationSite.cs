namespace EprRegisterEnrolBackend.DomainModel.Entities;

public class RegistrationSite
{
    public PostalAddress? Address { get; set; }
    public string? GridReference { get; set; }
    public string? SiteId { get; set; }
}
