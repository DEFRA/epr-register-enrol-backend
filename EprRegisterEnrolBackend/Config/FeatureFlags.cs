using EprRegisterEnrolBackend.DomainModel;

namespace EprRegisterEnrolBackend.Config;

/// <summary>
/// Configuration keys of the feature flags under <c>FeatureFlags</c>, and the startup checks of what each needs.
/// </summary>
public static class FeatureFlags
{
    /// <summary>
    /// RA-425 / RA-492: maps the in-year changes journey's endpoints (the registration status and the accreditation
    /// change requests) only when on; off by default, until that journey ships.
    /// </summary>
    public const string InYearChangesEnabledKey = "FeatureFlags:InYearChangesEnabled";

    private const string DualWriteKey = $"{DomainModelConfig.SectionName}:{nameof(DomainModelConfig.DualWrite)}";

    /// <summary>
    /// RA-697: the in-year changes routes read the domain model's registrations and ORSs. On CDP only dual-write
    /// (<c>DualWrite</c>, <c>DualWriteSeed</c> for seeded ones, and the <c>MigrateEnabled</c> migration, which
    /// itself needs <c>DualWrite</c>) fills those, so with the flag on and <c>DualWrite</c> off the pages would
    /// quietly show nothing; refuse to start instead. Necessary, not sufficient: a registration only appears once
    /// dual-write has written or migrated it. Development is exempt: there the dev seeds write the domain model
    /// directly.
    /// </summary>
    public static void EnsureInYearChangesHasDualWrite(
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        if (
            configuration.GetValue<bool>(InYearChangesEnabledKey)
            && !configuration.GetValue<bool>(DualWriteKey)
            && !environment.IsDevelopment()
        )
        {
            throw new InvalidOperationException(
                $"{InYearChangesEnabledKey} needs {DualWriteKey} outside Development: turn {DualWriteKey} on, or "
                    + $"{InYearChangesEnabledKey} off. The in-year changes routes read the domain model, which on CDP "
                    + "only dual-write fills."
            );
        }
    }
}
