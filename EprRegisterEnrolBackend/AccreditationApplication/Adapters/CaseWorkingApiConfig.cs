namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class CaseWorkingApiConfig
{
    public string Url { get; set; } = "http://localhost:8085";

    public string CognitoClientId { get; set; } = "epr-register-enrol-backend";

    public string? SharedSecret { get; set; }

    public bool UseStub { get; set; } = true;
}
