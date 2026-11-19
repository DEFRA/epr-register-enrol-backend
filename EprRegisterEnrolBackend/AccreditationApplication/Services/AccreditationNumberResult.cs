namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>RA-783: the number <see cref="AccreditationNumberRules.Generate"/> built, or why it
/// couldn't build one.</summary>
public sealed record AccreditationNumberResult
{
    private AccreditationNumberResult(
        AccreditationNumberCase? numberCase,
        string? accreditationNumber,
        string? error,
        bool noSequenceLeft
    )
    {
        Case = numberCase;
        AccreditationNumber = accreditationNumber;
        Error = error;
        NoSequenceLeft = noSequenceLeft;
    }

    /// <summary>Which rule built the number; null when it couldn't be built.</summary>
    public AccreditationNumberCase? Case { get; }

    /// <summary>The number; null when it couldn't be built.</summary>
    public string? AccreditationNumber { get; }

    /// <summary>Why the number couldn't be built; null when it was.</summary>
    public string? Error { get; }

    /// <summary>True when every sequence drawn was one the organisation already uses, so the
    /// sources were valid but no new number was found: a conflict, not bad data.</summary>
    public bool NoSequenceLeft { get; }

    public bool IsValid => Error is null;

    internal static AccreditationNumberResult Valid(
        AccreditationNumberCase numberCase,
        string accreditationNumber
    ) => new(numberCase, accreditationNumber, error: null, noSequenceLeft: false);

    internal static AccreditationNumberResult Invalid(string error) =>
        new(numberCase: null, accreditationNumber: null, error, noSequenceLeft: false);

    internal static AccreditationNumberResult SequencesExhausted(string error) =>
        new(numberCase: null, accreditationNumber: null, error, noSequenceLeft: true);
}
