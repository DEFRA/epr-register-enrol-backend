using Microsoft.Extensions.Options;

namespace EprRegisterEnrolBackend.Auth;

/// <summary>
/// Non-secret configuration for the service clients (for example ReEx) that read this
/// backend's data with client-id plus HMAC auth. Bound from the <c>ServiceClients</c> section.
/// Each client has its own client id here and its own secret in CDP secrets (see
/// <see cref="ServiceClientRegistry"/>), per environment.
/// </summary>
public class ServiceClientsConfig
{
    public const string SectionName = "ServiceClients";

    public List<ServiceClientDefinition> Clients { get; set; } = [];

    /// <summary>
    /// First accreditation year this backend is the source for. ReEx holds its own copy of
    /// earlier years, so earlier applications are never served to a service client.
    /// </summary>
    public int FirstAccreditationYear { get; set; } = 2027;
}

public class ServiceClientDefinition
{
    /// <summary>
    /// Short name, for example <c>reex</c>. Names the secret: <c>AUTH_SHARED_SECRET__REEX</c>
    /// (and <c>AUTH_SHARED_SECRET__REEX_NEXT</c> while the secret is being rotated).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The value the client sends in <c>x-cdp-client-id</c>.</summary>
    public string ClientId { get; set; } = string.Empty;
}

/// <summary>A configured service client with every secret it currently signs with.</summary>
public sealed record ServiceClient(string Name, string ClientId, IReadOnlyList<string> Secrets);

public interface IServiceClientRegistry
{
    bool TryGet(string clientId, out ServiceClient client);

    IReadOnlyCollection<ServiceClient> All { get; }
}

/// <summary>
/// Resolves each configured client's secrets once at startup. A secret named
/// <c>AUTH_SHARED_SECRET__{NAME}</c> is stored under the config key
/// <c>AUTH_SHARED_SECRET:{NAME}</c> (the environment provider rewrites <c>__</c> to <c>:</c>), the
/// same flat per-caller convention used for the other inbound callers. A second secret,
/// <c>AUTH_SHARED_SECRET__{NAME}_NEXT</c>, is accepted alongside it so a secret can be rotated
/// without the client ever being locked out.
/// A client with no secret is left out, so its requests fail closed.
/// </summary>
public sealed class ServiceClientRegistry : IServiceClientRegistry
{
    private readonly Dictionary<string, ServiceClient> _byClientId;

    public ServiceClientRegistry(
        IOptions<ServiceClientsConfig> options,
        IConfiguration configuration,
        ILogger<ServiceClientRegistry> logger
    )
    {
        _byClientId = new Dictionary<string, ServiceClient>(StringComparer.Ordinal);
        var seenClientIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in options.Value.Clients)
        {
            if (
                string.IsNullOrWhiteSpace(definition.Name)
                || string.IsNullOrWhiteSpace(definition.ClientId)
            )
            {
                throw new InvalidOperationException(
                    "Every ServiceClients client needs a Name and a ClientId."
                );
            }

            // Checked against every configured client, not just those with a secret, so a
            // duplicate cannot hide behind a client whose secret is not set yet.
            if (!seenClientIds.Add(definition.ClientId))
            {
                throw new InvalidOperationException(
                    $"ServiceClients has more than one client with ClientId '{definition.ClientId}'."
                );
            }

            var secretName = SecretConfigName(definition.Name);
            var secrets = new[]
            {
                configuration.GetValue<string>($"AUTH_SHARED_SECRET:{secretName}"),
                configuration.GetValue<string>($"AUTH_SHARED_SECRET:{secretName}_NEXT"),
            }
                .Where(secret => !string.IsNullOrEmpty(secret))
                .Select(secret => secret!)
                .ToList();

            if (secrets.Count == 0)
            {
                logger.LogError(
                    "Service client {ClientName} has no secret: AUTH_SHARED_SECRET__{SecretName} is not configured, so its requests will be rejected.",
                    definition.Name,
                    secretName
                );
                continue;
            }

            _byClientId[definition.ClientId] = new ServiceClient(
                definition.Name,
                definition.ClientId,
                secrets
            );
        }
    }

    public IReadOnlyCollection<ServiceClient> All => _byClientId.Values;

    public bool TryGet(string clientId, out ServiceClient client) =>
        _byClientId.TryGetValue(clientId, out client!);

    internal static string SecretConfigName(string clientName) =>
        clientName.Trim().ToUpperInvariant().Replace('-', '_');
}
