namespace EprRegisterEnrolBackend.DomainModel.Persistence;

public static class DomainModelCollections
{
    public const string LegacyApplications = "accreditationApplications";
    // RA-706 - with LegacyWrites off, the transaction-scoped copies a write runs the legacy persistence against; empty outside a transaction.
    public const string CutStagingApplications = "accreditationApplicationsCutStaging";
    public const string Organisations = "organisations";
    public const string RegistrationVersions = "registrationVersions";
    public const string OverseasSiteVersions = "overseasSiteVersions";
    public const string ApplicationVersions = "applicationVersions";
    public const string AccreditationVersions = "accreditationVersions";
    public const string MigrationConflicts = "migrationConflicts";
    public const string DomainModelMigrations = "domainModelMigrations";
}
