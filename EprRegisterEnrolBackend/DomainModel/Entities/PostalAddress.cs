namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>A structured postal address (design doc §3.2). Used by every entity that holds one.</summary>
public class PostalAddress
{
    public string? Line1 { get; set; }
    public string? Line2 { get; set; }
    public string? TownOrCity { get; set; }
    public string? Region { get; set; }
    public string? Postcode { get; set; }
    public string? Country { get; set; }
}
