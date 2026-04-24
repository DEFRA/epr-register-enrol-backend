namespace EprRegisterEnrolBackend.Auth.Models;

public class TokenRequest
{
    public string? grant_type { get; set; }
    public string? client_id { get; set; }
    public string? client_secret { get; set; }
}

public class TokenResponse
{
    public string access_token { get; set; }
    public string token_type { get; set; } = "Bearer";
    public int expires_in { get; set; }

    public TokenResponse(string accessToken, int expiresIn)
    {
        access_token = accessToken;
        expires_in = expiresIn;
    }
}

public class TokenErrorResponse
{
    public string error { get; set; }
    public string? error_description { get; set; }

    public TokenErrorResponse(string error, string? description = null)
    {
        this.error = error;
        error_description = description;
    }
}
