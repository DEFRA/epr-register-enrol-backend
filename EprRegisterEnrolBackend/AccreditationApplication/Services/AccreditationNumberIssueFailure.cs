namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>RA-783: why <see cref="AccreditationNumberIssuer"/> couldn't issue a number.</summary>
public enum AccreditationNumberIssueFailure
{
    /// <summary>The history is malformed or inconsistent, so no number can be built from it.</summary>
    InvalidHistory,

    /// <summary>The number is, or every number tried was, already held.</summary>
    AlreadyHeld,
}
