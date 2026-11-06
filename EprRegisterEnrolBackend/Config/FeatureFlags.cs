namespace EprRegisterEnrolBackend.Config;

/// <summary>Configuration keys of the feature flags under <c>FeatureFlags</c>.</summary>
public static class FeatureFlags
{
    /// <summary>
    /// RA-425 / RA-492: maps the in-year changes journey's endpoints (the registration status and the accreditation
    /// change requests) only when on; off by default, until that journey ships.
    /// </summary>
    public const string InYearChangesEnabledKey = "FeatureFlags:InYearChangesEnabled";
}
