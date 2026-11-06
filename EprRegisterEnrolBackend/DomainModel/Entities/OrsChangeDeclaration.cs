namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>Who made the in-year ORS change's declaration, and when.</summary>
public class OrsChangeDeclaration
{
    public required Submitter DeclaredBy { get; set; }

    public DateTime DeclaredAt { get; set; }
}
