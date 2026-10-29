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
    /// Run the B0 transaction smoke test and the B1 legacy-data survey once at startup. Only for test
    /// environments, where the data is test data (R15).
    /// </summary>
    public bool DiagnosticsEnabled { get; set; }
}
