namespace EprRegisterEnrolBackend.AccreditationApplication.Models;

// RA-448: parameters for IRegulatoryNumberGenerator.GenerateAsync, bundled into one
// object so the method itself stays under Sonar's 7-parameter limit (S107).
public record RegulatoryNumberSpec
{
    public required NumberType Type { get; init; }
    public required Nation Nation { get; init; }
    public required bool IsExporter { get; init; }
    public required int OrgId { get; init; }
    public required MaterialType Material { get; init; }
    public GlassRecyclingProcess? GlassRecyclingProcess { get; init; }
    public required int Year { get; init; }
}
