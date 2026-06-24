namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

public class CaseWorkingApiConfig
{
    /// <summary>Base URL of the case management backend (e.g. http://case-management-backend:8085).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Value sent in the x-cdp-cognito-client-id header to identify this service.</summary>
    public string ClientId { get; set; } = "epr-register-enrol-backend";

    /// <summary>
    /// HMAC-SHA256 shared secret for signing trust headers (ADR-0003).
    /// Required in non-Development environments; omit in local dev to use
    /// header-trust mode.
    /// </summary>
    public string? SharedSecret { get; set; }
}
