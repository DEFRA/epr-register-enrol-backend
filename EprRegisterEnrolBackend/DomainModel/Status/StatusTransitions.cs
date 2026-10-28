namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>
/// One state machine's legal transitions (design §3.4). Status is recorded time only, so a check compares the
/// current status with the new one and nothing else (§4.4). A status with no entry allows nothing, and staying
/// in the same status is not a transition.
/// </summary>
public sealed class StatusTransitions<TStatus>
    where TStatus : struct, Enum
{
    private readonly string _kind;
    private readonly Dictionary<TStatus, HashSet<TStatus>> _allowed;

    public StatusTransitions(string kind, IReadOnlyDictionary<TStatus, TStatus[]> allowed)
    {
        _kind = kind;
        _allowed = allowed.ToDictionary(p => p.Key, p => p.Value.ToHashSet());
    }

    public bool CanTransition(TStatus from, TStatus to) =>
        _allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    /// <exception cref="IllegalStatusTransitionException">The transition is not allowed.</exception>
    public void EnsureCanTransition(TStatus from, TStatus to)
    {
        if (!CanTransition(from, to))
        {
            throw new IllegalStatusTransitionException(_kind, from.ToString(), to.ToString());
        }
    }
}
