namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class ApplicationReferenceService : IApplicationReferenceService
{
    public string Generate(int year)
    {
        var suffix = Guid.NewGuid().ToString("N")[..7].ToUpper();
        return $"EPR-ACC-{year}-{suffix}";
    }
}
