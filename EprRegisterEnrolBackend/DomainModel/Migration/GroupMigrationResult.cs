namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>What one ensure-migrated call did; the migration service (B7) records it as its checkpoint.</summary>
public sealed record GroupMigrationResult(
    GroupMigrationOutcome Outcome,
    int MigratedApplications,
    IReadOnlyList<MigrationConflict> Conflicts
)
{
    public static GroupMigrationResult NotFound { get; } =
        new(GroupMigrationOutcome.ApplicationNotFound, 0, []);

    public static GroupMigrationResult NothingToDo { get; } =
        new(GroupMigrationOutcome.AlreadyMigrated, 0, []);
}

public enum GroupMigrationOutcome
{
    /// <summary>No legacy application has the id; the write path answers its own 404.</summary>
    ApplicationNotFound,

    /// <summary>Every application in the group already has a current version: nothing was written.</summary>
    AlreadyMigrated,

    /// <summary>The group's missing entities were written in the caller's transaction.</summary>
    Migrated,
}
