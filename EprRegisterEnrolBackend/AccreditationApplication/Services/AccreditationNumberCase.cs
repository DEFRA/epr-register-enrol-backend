namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>RA-783: which rule builds an accreditation number (see <see cref="AccreditationNumberRules"/>).</summary>
public enum AccreditationNumberCase
{
    ContinuousRenewal,
    FirstTime,
    RenewalAfterBreak,
}
