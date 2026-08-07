namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class CaseManagementAuthConfig
{
    /// <summary>
    /// Sourced from the flat <c>OPERATOR_BACKEND_SHARED_SECRET</c> env var, not
    /// a nested <c>CaseManagementAuth__*</c> key — see Program.cs's binding.
    /// </summary>
    public string? SharedSecret { get; set; }

    public string ExpectedCognitoClientId { get; set; } = "epr-register-enrol-management-be";
}
