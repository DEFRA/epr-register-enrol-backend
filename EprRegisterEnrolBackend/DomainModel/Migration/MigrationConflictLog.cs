namespace EprRegisterEnrolBackend.DomainModel.Migration;

internal static class MigrationConflictLog
{
    /// <summary>
    /// One line per conflict, because production can't read <c>migrationConflicts</c> (design doc R18). Call it once
    /// the transaction has committed, so a rollback or retry never reports a conflict that wasn't saved. Ids, ORS ids
    /// and site numbers only, as on the conflict itself.
    /// </summary>
    public static void LogRecorded(this ILogger logger, IEnumerable<MigrationConflict> conflicts)
    {
        foreach (var conflict in conflicts)
        {
            logger.LogWarning(
                "Migration conflict recorded: kind {Kind}, registration {ReExRegistrationId}, site {OrsId}, site number {SiteNumber}, applications {ApplicationIds}, winner {WinnerApplicationId}",
                conflict.Kind,
                conflict.ReExRegistrationId,
                conflict.OrsId,
                conflict.SiteNumber,
                string.Join(",", conflict.ApplicationIds),
                conflict.WinnerApplicationId
            );
        }
    }
}
