namespace EprRegisterEnrolBackend.DomainModel.Entities;

public class RegistrationSite
{
    public PostalAddress? Address { get; set; }

    /// <summary>The site's address as one string, which is all a seed or a legacy application holds.</summary>
    public string? FormattedAddress { get; set; }

    public string? GridReference { get; set; }
    public string? SiteId { get; set; }
}
