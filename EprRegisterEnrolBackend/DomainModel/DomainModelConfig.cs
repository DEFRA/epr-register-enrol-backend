namespace EprRegisterEnrolBackend.DomainModel;

/// <summary>
/// The <c>DomainModel:*</c> flags (design doc §6). Each phase ships behind its own flag, off by default, so
/// deploying the code changes nothing until an environment opts in.
/// </summary>
public class DomainModelConfig
{
    public const string SectionName = "DomainModel";

    /// <summary>
    /// Phase 1, "Expand": build the empty domain model collections and their indexes at startup, and gate
    /// <c>/health/ready</c> on them. Turning it off again is the rollback; the empty collections can be left.
    /// </summary>
    public bool ExpandEnabled { get; set; }

    /// <summary>
    /// Phase 2, "Seed dual-write": Seed also writes the Organisation, Registration and overseas sites, and stamps
    /// their ids on the application. Needs <see cref="ExpandEnabled"/>. A failure is logged and counted, and the
    /// seed still succeeds. Turning it off is the rollback; the stamped ids are ignored by legacy code.
    /// </summary>
    public bool DualWriteSeed { get; set; }

    /// <summary>
    /// Phase 3, "Full dual-write": the write paths write the legacy application and the domain model in one
    /// transaction, and a model-write failure fails the request and rolls back the legacy write (D9). Needs
    /// <see cref="ExpandEnabled"/>. Turning it off is the rollback.
    /// </summary>
    public bool DualWrite { get; set; }

    /// <summary>
    /// Phase 4, "Migrate": move every legacy application not yet in the domain model, one registration group per
    /// transaction, in the background at each startup. Needs <see cref="DualWrite"/>, so no write can slip past a
    /// group once it is migrated. Turn it on a few days after dual-write, and off again to stop it.
    /// </summary>
    public bool MigrateEnabled { get; set; }

    /// <summary>
    /// Phase 4, "Reconcile": compare every migrated application with its legacy document and log a report with no
    /// personal data, at startup and every few hours. Needs <see cref="DualWrite"/>. For test environments: it never
    /// runs in live, whatever this is set to.
    /// </summary>
    public bool ReconcileEnabled { get; set; }

    /// <summary>
    /// Run the B0 transaction smoke test and the B1 legacy-data survey once at startup. Only for test
    /// environments, where the data is test data (R15).
    /// </summary>
    public bool DiagnosticsEnabled { get; set; }
}
