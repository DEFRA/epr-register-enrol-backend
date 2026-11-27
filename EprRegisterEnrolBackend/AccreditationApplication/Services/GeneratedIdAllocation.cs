using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// What a generated identifier needs to know about its kind: where its scope comes from, how a new record claiming it
/// is built and attached, and which guarded write persists it. ORS ids (RA-482) and interim site numbers (RA-603)
/// differ only in these, so the retry rules around them exist once (<see cref="SiteIdAllocation.AllocateGeneratedIdAsync{T}"/>).
/// </summary>
public sealed record GeneratedIdAllocation<T>(
    string CapacityNoun,
    string IdName,
    Func<AccreditationApplicationModel, Task<IEnumerable<string?>>> Scope,
    Func<AccreditationApplicationModel, string, (IResult? Error, T? Created)> Attach,
    Func<AccreditationApplicationModel, string, Task<AccreditationApplicationModel?>> WriteIfAbsent
)
    where T : class;
