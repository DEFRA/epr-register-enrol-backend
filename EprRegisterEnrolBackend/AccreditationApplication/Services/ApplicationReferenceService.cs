namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

public class ApplicationReferenceService : IApplicationReferenceService
{
    public string Generate(int year)
    {
        //ToDo: find out how these references are generated, this is not correct
        var suffix = Guid.NewGuid().ToString("N")[..7].ToUpper();
        return $"EPR-ACC-{year}-{suffix}";
    }
}
