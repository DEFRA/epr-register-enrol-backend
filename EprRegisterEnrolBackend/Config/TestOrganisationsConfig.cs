namespace EprRegisterEnrolBackend.Config;

/// <summary>
/// Bound from the <c>TestOrganisations</c> section (env var <c>TestOrganisations__Enabled</c>).
/// When enabled, an organisationId starting with <see cref="OrganisationIdPrefix"/> is an E2E
/// test organisation: its ReEx lookups are answered by <c>StubReExApiAdapter</c> rather than
/// the real ReEx API (see <c>TestOrganisationReExApiAdapter</c>), and the
/// <c>epr-register-enrol-mgmt-tests</c> caller's <c>AUTH_SHARED_SECRET__MGMT_TESTS</c> Bearer is
/// accepted on Frontend-authenticated routes. Defaults to off, and the service refuses to start
/// with it on in CDP prod (<see cref="EnsureNotEnabledInProduction"/>).
/// </summary>
public class TestOrganisationsConfig
{
    public const string SectionName = "TestOrganisations";

    /// <summary>Lowercase, ordinal: <c>E2E-</c> is not a test organisation.</summary>
    public const string OrganisationIdPrefix = "e2e-";

    /// <summary>
    /// CDP platform environment name (<c>dev</c>, <c>test</c>, <c>perf-test</c>,
    /// <c>ext-test</c>, <c>prod</c>, ...). Read straight from the process environment rather
    /// than <c>IConfiguration</c>: <c>ENVIRONMENT</c> is also <c>WebHostDefaults.EnvironmentKey</c>,
    /// so whenever the ASP.NET host environment is set explicitly, host configuration shadows
    /// the key and <c>configuration["ENVIRONMENT"]</c> returns e.g. "Development" instead of the
    /// platform value (same reasoning as management-be's NotifySendingPolicy).
    /// </summary>
    public const string CdpEnvironmentVariable = "ENVIRONMENT";

    public const string ProductionCdpEnvironment = "prod";

    public bool Enabled { get; set; }

    public static bool IsTestOrganisation(string? organisationId) =>
        organisationId?.StartsWith(OrganisationIdPrefix, StringComparison.Ordinal) == true;

    public static string? ReadCdpEnvironment() =>
        System.Environment.GetEnvironmentVariable(CdpEnvironmentVariable);

    /// <summary>
    /// Startup guard (RA-000 AC8): test organisations and the mgmt-tests caller must never be
    /// switchable on in prod, whatever the config says.
    /// </summary>
    public static void EnsureNotEnabledInProduction(bool enabled, string? cdpEnvironment)
    {
        if (
            enabled
            && string.Equals(
                cdpEnvironment?.Trim(),
                ProductionCdpEnvironment,
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidOperationException(
                "TestOrganisations__Enabled must not be true in the CDP prod environment."
            );
    }
}
