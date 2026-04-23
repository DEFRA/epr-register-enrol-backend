using System.Diagnostics.CodeAnalysis;

namespace EprRegisterEnrolBackend.Auth.Config;

[ExcludeFromCodeCoverage]
public class ClientCredentialsConfig
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
