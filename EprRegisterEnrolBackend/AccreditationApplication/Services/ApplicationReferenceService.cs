namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class ApplicationReferenceService : IApplicationReferenceService
{
    public string Generate(int year)
    {
        // RA-196: references follow the format RA-######### (9 digits).
        var digits = Random.Shared.Next(100_000_000, 1_000_000_000);
        return $"RA-{digits}";
    }
}
