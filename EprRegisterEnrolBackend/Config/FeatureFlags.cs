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
    /// RA-705: serves every read from the domain model. See <see cref="ReadFromModelPrerequisites"/>.
    /// </summary>
    public const string ReadFromModelKey =
        $"{DomainModelConfig.SectionName}:{nameof(DomainModelConfig.ReadFromModel)}";

    private const string ExpandEnabledKey =
        $"{DomainModelConfig.SectionName}:{nameof(DomainModelConfig.ExpandEnabled)}";

    private const string DualWriteSeedKey =
        $"{DomainModelConfig.SectionName}:{nameof(DomainModelConfig.DualWriteSeed)}";

    /// <summary>
    /// The one place that lists ReadFromModel's prerequisites.
    /// </summary>
    private static readonly (string Key, string Reason)[] ReadFromModelPrerequisites =
        [
            (DualWriteKey, "On CDP only dual-write keeps the model current."),
            (DualWriteSeedKey, "Only seed dual-write writes a new application's initial version to the model."),
            (ExpandEnabledKey, "On CDP only expand creates the model collections."),
        ];

    /// <summary>
    /// RA-706: cut legacy writes.
    /// </summary>
    public const string LegacyWritesKey = $"{DomainModelConfig.SectionName}:{nameof(DomainModelConfig.LegacyWrites)}";

    private static readonly (string Key, string Reason)[] LegacyWritesCutPrerequisites =
        [
            (ReadFromModelKey, "Legacy is frozen, so every read must come from the model."),
            (DualWriteKey, "Only dual-write writes the model."),
            (DualWriteSeedKey, "Only seed dual-write writes a new application's initial version to the model."),
            (ExpandEnabledKey, "On CDP only expand creates the model collections."),
        ];

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

    /// <summary>
    /// RA-705: ReadFromModel serves every read from the domain model. See <see cref="ReadFromModelPrerequisites"/>.
    /// Development is exempt: there the dev seeds write the model directly.
    /// </summary>
    public static void EnsureReadFromModelHasItsPrerequisites(
        IConfiguration configuration,
        IHostEnvironment environment
    )
    {
        if (configuration.GetValue<bool>(ReadFromModelKey) && !environment.IsDevelopment())
        {
            foreach (var (key, reason) in ReadFromModelPrerequisites)
            {
                if (!configuration.GetValue<bool>(key))
                {
                    throw new InvalidOperationException(
                        $"{ReadFromModelKey} needs {key} outside Development: turn {key} on, or "
                            + $"{ReadFromModelKey} off. {reason}"
                    );
                }
            }
        }
    }

    /// <summary>
    /// RA-706: LegacyWrites off is the cut. There is no Development exemption because a frozen legacy read,
    /// or a write that reaches neither store, is wrong locally too. It also requires InYearChangesEnabled off.
    /// </summary>
    public static void EnsureLegacyWritesCutHasItsPrerequisites(IConfiguration configuration)
    {
        if (!configuration.GetValue(LegacyWritesKey, true))
        {
            foreach (var (key, reason) in LegacyWritesCutPrerequisites)
            {
                if (!configuration.GetValue<bool>(key))
                {
                    throw new InvalidOperationException(
                        $"{LegacyWritesKey} off needs {key}: turn {key} on, or {LegacyWritesKey} back on. {reason}"
                    );
                }
            }

            if (configuration.GetValue<bool>(InYearChangesEnabledKey))
            {
                throw new InvalidOperationException(
                    $"{LegacyWritesKey} off needs {InYearChangesEnabledKey} off: turn "
                        + $"{InYearChangesEnabledKey} off, or {LegacyWritesKey} back on. "
                        + "The in-year changes routes still read the legacy collection, "
                        + "which the cut freezes."
                );
            }
        }
    }
}
