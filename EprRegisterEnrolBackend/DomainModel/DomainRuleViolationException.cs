namespace EprRegisterEnrolBackend.DomainModel;

/// <summary>
/// A write the domain model refuses by rule (design doc D9), as opposed to a fault. It fails every retry the same way,
/// so the unit of work answers 409 with <see cref="Rule"/> rather than 500. The rule is a fixed code, safe to log and
/// to return; the message may name ids and is never logged.
/// </summary>
public sealed class DomainRuleViolationException(DomainRule rule, string message)
    : InvalidOperationException(message)
{
    public DomainRule Rule { get; } = rule;
}

/// <summary>The rules a write can break. The names are returned to the caller and logged.</summary>
public enum DomainRule
{
    /// <summary>An application was approved without an accreditation number.</summary>
    AccreditationNumberMissing,

    /// <summary>The registration already has an Accreditation for the application's year.</summary>
    YearAlreadyAccredited,
}
