using EprRegisterEnrolBackend.Auth;

namespace EprRegisterEnrolBackend.Test.Auth;

/// <summary>
/// Signs requests the way a service client does: the v3 canonical payload, HMAC-SHA256, the same
/// scheme ManagementBe uses. The signing is done with the handler's own
/// <c>ComputeSignature</c>, so these tests and the handler cannot drift apart; the worked example
/// in the onboarding guide is checked against it separately.
/// </summary>
internal static class ServiceClientSigner
{
    public static string Timestamp(DateTime? at = null) =>
        (at ?? DateTime.UtcNow).ToString("yyyy-MM-ddTHH:mm:ssZ");

    public static string NewNonce() => Convert.ToBase64String(Guid.NewGuid().ToByteArray());

    public static Dictionary<string, string> Headers(
        string clientId,
        string secret,
        string? timestamp = null,
        string? nonce = null,
        string? signatureOverride = null
    )
    {
        timestamp ??= Timestamp();
        nonce ??= NewNonce();
        return new Dictionary<string, string>
        {
            ["x-cdp-client-id"] = clientId,
            ["x-cdp-auth-timestamp"] = timestamp,
            ["x-cdp-auth-nonce"] = nonce,
            ["x-cdp-auth-signature"] =
                signatureOverride
                ?? CaseManagementAuthenticationHandler.ComputeSignature(
                    secret,
                    clientId,
                    null,
                    null,
                    timestamp,
                    nonce
                ),
        };
    }

    public static HttpRequestMessage Request(
        HttpMethod method,
        string url,
        string clientId,
        string secret,
        string? timestamp = null,
        string? nonce = null,
        string? signatureOverride = null
    )
    {
        var request = new HttpRequestMessage(method, url);
        foreach (
            var (name, value) in Headers(clientId, secret, timestamp, nonce, signatureOverride)
        )
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        return request;
    }
}
