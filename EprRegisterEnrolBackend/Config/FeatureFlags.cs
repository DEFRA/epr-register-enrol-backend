using EprRegisterEnrolBackend.DomainModel;

namespace EprRegisterEnrolBackend.Config;

/// <summary>Configuration keys of the feature flags under <c>FeatureFlags</c>.</summary>
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
    /// fills those, so with the flag on and dual-write off the pages would quietly show nothing; refuse to start
    /// instead. Development is exempt: there the dev seeds write the domain model directly.
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
                $"{InYearChangesEnabledKey} needs {DualWriteKey} outside Development: the in-year changes routes read "
                    + "the domain model, which only dual-write fills."
            );
        }
    }
}
