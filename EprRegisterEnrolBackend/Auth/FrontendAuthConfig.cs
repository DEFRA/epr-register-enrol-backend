namespace EprRegisterEnrolBackend.Auth;

public class FrontendAuthConfig
{
    /// <summary>
    /// Sourced from the flat <c>AUTH_SHARED_SECRET__FRONTEND</c> env var
    /// (looked up via its config-key colon form,
    /// <c>AUTH_SHARED_SECRET:FRONTEND</c>), not a nested
    /// <c>FrontendAuth__*</c> key — see Program.cs's binding, and
    /// CaseManagementAuthConfig for the same convention.
    /// </summary>
    public string? SharedSecret { get; set; }
}
