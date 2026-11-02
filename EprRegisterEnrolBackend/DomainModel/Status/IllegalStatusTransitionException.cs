namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>A status change that the entity's state machine (design §3.4) does not allow.</summary>
public sealed class IllegalStatusTransitionException(string kind, string from, string to)
    : InvalidOperationException($"Cannot transition {kind} status from {from} to {to}.")
{
    /// <summary>What kind of status it is, such as <c>accreditation</c>.</summary>
    public string Kind { get; } = kind;

    public string From { get; } = from;

    public string To { get; } = to;
}
