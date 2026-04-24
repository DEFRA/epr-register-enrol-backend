using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;
using EprRegisterEnrolBackend.Auth.Config;
using EprRegisterEnrolBackend.Auth.Models;

namespace EprRegisterEnrolBackend.Auth.Services;

public interface ITokenService
{
    TokenResponse? GenerateToken(string clientId, string clientSecret);
    bool ValidateClientCredentials(string clientId, string clientSecret);
}

public class TokenService : ITokenService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<TokenService> _logger;

    public TokenService(IConfiguration configuration, ILogger<TokenService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public bool ValidateClientCredentials(string clientId, string clientSecret)
    {
        // Read from environment variables first, then fall back to config
        var configuredClientId = Environment.GetEnvironmentVariable("CLIENT_ID") 
            ?? _configuration.GetValue<string>("OAuth:ClientId");
        var configuredClientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET") 
            ?? _configuration.GetValue<string>("OAuth:ClientSecret");

        var isValid = !string.IsNullOrWhiteSpace(configuredClientId) &&
                      !string.IsNullOrWhiteSpace(configuredClientSecret) &&
                      string.Equals(clientId, configuredClientId, StringComparison.Ordinal) &&
                      string.Equals(clientSecret, configuredClientSecret, StringComparison.Ordinal);

        if (!isValid)
        {
            _logger.LogWarning("Invalid client credentials attempt for client_id: {ClientId}", clientId);
        }

        return isValid;
    }

    public TokenResponse? GenerateToken(string clientId, string clientSecret)
    {
        if (!ValidateClientCredentials(clientId, clientSecret))
        {
            return null;
        }

        // Generate JWT token
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = GetSigningKey();
        var expiryMinutes = _configuration.GetValue<int>("OAuth:TokenExpiryMinutes", 60);

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.NameIdentifier, clientId),
            new Claim("client_id", clientId),
            new Claim("scope", "api")
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(expiryMinutes),
            Issuer = _configuration.GetValue<string>("Authentication:Authority") ?? "api",
            Audience = _configuration.GetValue<string>("Authentication:Audience") ?? "api",
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        var accessToken = tokenHandler.WriteToken(token);

        _logger.LogInformation("Token generated successfully for client_id: {ClientId}", clientId);

        return new TokenResponse(accessToken, expiryMinutes * 60);
    }

    private SymmetricSecurityKey GetSigningKey()
    {
        var signingKey = _configuration.GetValue<string>("OAuth:SigningKey");
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            // Default key for development only (should be overridden in production)
            signingKey = "your-256-bit-secret-key-change-this-in-production-12345";
        }

        // Ensure key is at least 32 bytes (256 bits) for HS256
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(signingKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException("OAuth:SigningKey must be at least 32 bytes (256 bits)");
        }

        return new SymmetricSecurityKey(keyBytes.Take(32).ToArray());
    }
}
