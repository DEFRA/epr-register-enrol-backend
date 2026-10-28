namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>ReEx's partnership details, for an organisation that is a partnership.</summary>
public class Partnership
{
    /// <summary>ReEx's partnership type, such as <c>ltd</c> or <c>ltd_liability</c>.</summary>
    public string? Type { get; set; }

    public List<Partner> Partners { get; set; } = [];
}
