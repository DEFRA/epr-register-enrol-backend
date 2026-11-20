namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>RA-783: the number <see cref="AccreditationNumberIssuer"/> issued, or why it
/// couldn't.</summary>
public sealed record AccreditationNumberIssueOutcome
{
    private AccreditationNumberIssueOutcome(
        IssuedAccreditationNumber? issued,
        AccreditationNumberIssueFailure? failure,
        string? failureMessage
    )
    {
        Issued = issued;
        Failure = failure;
        FailureMessage = failureMessage;
    }

    /// <summary>The number issued; null on failure.</summary>
    public IssuedAccreditationNumber? Issued { get; }

    /// <summary>Why no number was issued; null on success.</summary>
    public AccreditationNumberIssueFailure? Failure { get; }

    /// <summary>The failure, for the caller's response; null on success.</summary>
    public string? FailureMessage { get; }

    internal static AccreditationNumberIssueOutcome Success(IssuedAccreditationNumber issued) =>
        new(issued, failure: null, failureMessage: null);

    internal static AccreditationNumberIssueOutcome Failed(
        AccreditationNumberIssueFailure failure,
        string message
    ) => new(issued: null, failure, message);
}
