namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-783: the random digits in a first-time or after-a-break accreditation number's sequence
/// (see <see cref="AccreditationNumberRules"/>). An interface so tests can choose the digits.
/// </summary>
public interface IAccreditationSequenceRandom
{
    /// <summary>A random number from 0 to 999.</summary>
    int NextBelow1000();
}
