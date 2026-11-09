namespace EprRegisterEnrolBackend.DomainModel.Versioning;

public static class VersionSentinel
{
    /// <summary>
    /// The <c>validTo</c> of the current version: 9999-12-31T23:59:59.999Z, the largest BSON date. The driver
    /// reads that value back as <see cref="DateTime.MaxValue"/>, so this is the same value, not an approximation.
    /// </summary>
    public static readonly DateTime ValidTo = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
}
