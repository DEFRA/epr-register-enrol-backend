namespace EprRegisterEnrolBackend.AccreditationApplication.Adapters;

/// <summary>
/// RA-682: a change request holding adds and updates is sent to ManagementBe as one submission per change type. This
/// is thrown when an earlier group was accepted and a later one failed: ManagementBe holds
/// <see cref="HeldChangeTypes"/>, and <see cref="RefusedMayBeHeld"/> says whether it may also hold the group that
/// failed (a timeout or a 5xx), or certainly doesn't (a 4xx it returns before saving anything).
/// </summary>
public sealed class ChangeRequestPartlySubmittedException(
    IReadOnlyCollection<string> heldChangeTypes,
    string refusedChangeType,
    bool refusedMayBeHeld,
    Exception innerException
)
    : HttpRequestException(
        $"ManagementBe holds the {string.Join(", ", heldChangeTypes)} sites of a change request "
            + $"but did not accept its {refusedChangeType} sites.",
        innerException
    )
{
    public IReadOnlyCollection<string> HeldChangeTypes { get; } = heldChangeTypes;
    public string RefusedChangeType { get; } = refusedChangeType;
    public bool RefusedMayBeHeld { get; } = refusedMayBeHeld;
}
