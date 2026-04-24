using EprRegisterEnrolBackend.Auth.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace EprRegisterEnrolBackend.Test.Auth.Services;

public class TokenServiceTests : IDisposable
{
    private const string ValidClientId = "my-client";
    private const string ValidClientSecret = "my-secret";
    private const string ValidSigningKey = "a-valid-signing-key-at-least-32-bytes!!";

    private readonly string? _savedClientId;
    private readonly string? _savedClientSecret;

    public TokenServiceTests()
    {
        _savedClientId = Environment.GetEnvironmentVariable("CLIENT_ID");
        _savedClientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
        Environment.SetEnvironmentVariable("CLIENT_ID", null);
        Environment.SetEnvironmentVariable("CLIENT_SECRET", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLIENT_ID", _savedClientId);
        Environment.SetEnvironmentVariable("CLIENT_SECRET", _savedClientSecret);
    }

    private static ITokenService CreateService(Dictionary<string, string?> config, ILogger<TokenService>? logger = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config)
            .Build();
        logger ??= Substitute.For<ILogger<TokenService>>();
        return new TokenService(configuration, logger);
    }

    private static Dictionary<string, string?> ValidConfig() => new()
    {
        ["OAuth:ClientId"] = ValidClientId,
        ["OAuth:ClientSecret"] = ValidClientSecret,
        ["OAuth:SigningKey"] = ValidSigningKey,
        ["OAuth:TokenExpiryMinutes"] = "60",
        ["Authentication:Audience"] = "api",
    };

    #region ValidateClientCredentials

    [Fact]
    public void ValidateClientCredentials_MatchingCredentials_ReturnsTrue()
    {
        var service = CreateService(ValidConfig());

        service.ValidateClientCredentials(ValidClientId, ValidClientSecret).Should().BeTrue();
    }

    [Fact]
    public void ValidateClientCredentials_WrongClientId_ReturnsFalse()
    {
        var service = CreateService(ValidConfig());

        service.ValidateClientCredentials("wrong-client", ValidClientSecret).Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_WrongClientSecret_ReturnsFalse()
    {
        var service = CreateService(ValidConfig());

        service.ValidateClientCredentials(ValidClientId, "wrong-secret").Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_EmptyClientId_ReturnsFalse()
    {
        var service = CreateService(ValidConfig());

        service.ValidateClientCredentials("", ValidClientSecret).Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_EmptyClientSecret_ReturnsFalse()
    {
        var service = CreateService(ValidConfig());

        service.ValidateClientCredentials(ValidClientId, "").Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_ConfigClientIdIsEmpty_ReturnsFalse()
    {
        var config = ValidConfig();
        config["OAuth:ClientId"] = "";
        var service = CreateService(config);

        service.ValidateClientCredentials(ValidClientId, ValidClientSecret).Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_ConfigClientSecretIsEmpty_ReturnsFalse()
    {
        var config = ValidConfig();
        config["OAuth:ClientSecret"] = "";
        var service = CreateService(config);

        service.ValidateClientCredentials(ValidClientId, ValidClientSecret).Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_ConfigClientIdIsNull_ReturnsFalse()
    {
        var config = ValidConfig();
        config["OAuth:ClientId"] = null;
        var service = CreateService(config);

        service.ValidateClientCredentials(ValidClientId, ValidClientSecret).Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_EnvVarTakesPrecedenceOverConfig()
    {
        Environment.SetEnvironmentVariable("CLIENT_ID", "env-client");
        Environment.SetEnvironmentVariable("CLIENT_SECRET", "env-secret");
        var service = CreateService(ValidConfig()); // config has "my-client"/"my-secret"

        service.ValidateClientCredentials("env-client", "env-secret").Should().BeTrue();
        service.ValidateClientCredentials(ValidClientId, ValidClientSecret).Should().BeFalse();
    }

    [Fact]
    public void ValidateClientCredentials_CredentialsAreCaseSensitive()
    {
        var service = CreateService(ValidConfig());

        service.ValidateClientCredentials(ValidClientId.ToUpper(), ValidClientSecret).Should().BeFalse();
        service.ValidateClientCredentials(ValidClientId, ValidClientSecret.ToUpper()).Should().BeFalse();
    }

    #endregion

    #region GenerateToken

    [Fact]
    public void GenerateToken_ValidCredentials_ReturnsTokenResponse()
    {
        var service = CreateService(ValidConfig());

        var result = service.GenerateToken(ValidClientId, ValidClientSecret);

        result.Should().NotBeNull();
        result!.token_type.Should().Be("Bearer");
        result.access_token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GenerateToken_InvalidCredentials_ReturnsNull()
    {
        var service = CreateService(ValidConfig());

        service.GenerateToken("bad-client", "bad-secret").Should().BeNull();
    }

    [Fact]
    public void GenerateToken_ExpiresInReflectsConfiguredMinutes()
    {
        var config = ValidConfig();
        config["OAuth:TokenExpiryMinutes"] = "45";
        var service = CreateService(config);

        var result = service.GenerateToken(ValidClientId, ValidClientSecret);

        result!.expires_in.Should().Be(45 * 60);
    }

    [Fact]
    public void GenerateToken_DefaultExpiryIs3600SecondsWhenNotConfigured()
    {
        var config = ValidConfig();
        config.Remove("OAuth:TokenExpiryMinutes");
        var service = CreateService(config);

        var result = service.GenerateToken(ValidClientId, ValidClientSecret);

        result!.expires_in.Should().Be(3600);
    }

    [Fact]
    public void GenerateToken_ProducesValidJwtWithExpectedClaims()
    {
        var service = CreateService(ValidConfig());

        var result = service.GenerateToken(ValidClientId, ValidClientSecret);

        var handler = new JwtSecurityTokenHandler();
        handler.CanReadToken(result!.access_token).Should().BeTrue();

        var token = handler.ReadJwtToken(result.access_token);
        token.Claims.Should().Contain(c => c.Type == "client_id" && c.Value == ValidClientId);
        token.Claims.Should().Contain(c => c.Type == "scope" && c.Value == "api");
    }

    [Fact]
    public void GenerateToken_TokenContainsConfiguredAudience()
    {
        var config = ValidConfig();
        config["Authentication:Audience"] = "my-audience";
        var service = CreateService(config);

        var result = service.GenerateToken(ValidClientId, ValidClientSecret);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(result!.access_token);
        token.Audiences.Should().Contain("my-audience");
    }

    [Fact]
    public void GenerateToken_TokenUsesDefaultAudienceWhenNotConfigured()
    {
        var config = ValidConfig();
        config["Authentication:Audience"] = null;
        var service = CreateService(config);

        var result = service.GenerateToken(ValidClientId, ValidClientSecret);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(result!.access_token);
        token.Audiences.Should().Contain("api");
    }

    [Fact]
    public void GenerateToken_TokenIsNotExpiredImmediatelyAfterCreation()
    {
        var service = CreateService(ValidConfig());

        var result = service.GenerateToken(ValidClientId, ValidClientSecret);

        var token = new JwtSecurityTokenHandler().ReadJwtToken(result!.access_token);
        token.ValidTo.Should().BeAfter(DateTime.UtcNow);
    }

    #endregion

    #region Signing Key Validation

    [Fact]
    public void GenerateToken_ShortSigningKey_ThrowsInvalidOperationException()
    {
        var config = ValidConfig();
        config["OAuth:SigningKey"] = "tooshort"; // 8 bytes, < 32
        var service = CreateService(config);

        var act = () => service.GenerateToken(ValidClientId, ValidClientSecret);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32 bytes*");
    }

    [Fact]
    public void GenerateToken_NullSigningKey_UsesDefaultAndDoesNotThrow()
    {
        var config = ValidConfig();
        config["OAuth:SigningKey"] = null;
        var service = CreateService(config);

        var act = () => service.GenerateToken(ValidClientId, ValidClientSecret);

        act.Should().NotThrow();
    }

    [Fact]
    public void GenerateToken_Exactly32ByteSigningKey_DoesNotThrow()
    {
        var config = ValidConfig();
        config["OAuth:SigningKey"] = "exactly-32-bytes-signing-key-here"; // 33 chars, safe
        var service = CreateService(config);

        var act = () => service.GenerateToken(ValidClientId, ValidClientSecret);

        act.Should().NotThrow();
    }

    #endregion
}
