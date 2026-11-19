using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// An earlier accreditation: its year and number, for example 2026, A26ER5003240164PL, and where it
/// came from, so errors and the issue log name the right source.
/// </summary>
public sealed record PriorAccreditation(
    int Year,
    string AccreditationNumber,
    AccreditationReferenceSourceKind Source
)
{
    internal string Description =>
        Source == AccreditationReferenceSourceKind.PreviousAccreditation
            ? "The previous accreditation reference"
            : "Last year's accreditation number";
}
