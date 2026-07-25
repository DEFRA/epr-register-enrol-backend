namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class CaseManagementAuthConfig
{
    public string? SharedSecret { get; set; }

    public string ExpectedCognitoClientId { get; set; } = "epr-register-enrol-management-be";
}
