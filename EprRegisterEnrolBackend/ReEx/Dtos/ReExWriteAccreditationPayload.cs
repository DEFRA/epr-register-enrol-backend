namespace EprRegisterEnrolBackend.ReEx.Dtos;

public class ReExWriteAccreditationPayload
{
    public required string ApplicationId { get; init; }
    public required string OrganisationId { get; init; }
    public required string MaterialType { get; init; }
    public required int Year { get; init; }
    public string? SiteId { get; init; }
    public required string ApplicationReference { get; init; }
}
