using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// The <see cref="ApplicationEntity.ExclusivityKey"/> an application holds while it is open. Each application type
/// owns its own key and release rule on its <see cref="ApplicationDetails"/> class, in the formats and with the
/// release rules of register-enrol-application-types.md ("Rules and indexes"); this is the one place that asks them.
/// Application code chooses the rule by type, and the partial unique index on the key only catches two writers that
/// both passed the check at once. Nothing writes the key yet: writing it on accreditation applications would refuse a
/// second live application that legacy allows today, so it waits for the one-live-application rule (design doc §7 R12).
/// </summary>
public static class ExclusivityKeys
{
    /// <summary>The key the application should hold now: its key while it holds one, otherwise null.</summary>
    public static string? For(ApplicationEntity application) =>
        HoldsKey(application.Details, application.Status) ? Build(application) : null;

    /// <summary>The application's key whatever its status, or null when its type has none or a part is missing.</summary>
    public static string? Build(ApplicationEntity application) =>
        application.Details.ExclusivityKey(application);

    /// <summary>
    /// Whether an application with these details at <paramref name="status"/> still holds its key. Every type
    /// releases it when rejected or withdrawn. On approval it depends on the type: in-year changes release it,
    /// because the next change to the same ORS or Accreditation is a new application, and the others keep it.
    /// </summary>
    public static bool HoldsKey(ApplicationDetails details, ApplicationStatus status) =>
        status switch
        {
            ApplicationStatus.Rejected or ApplicationStatus.Withdrawn => false,
            ApplicationStatus.Approved => !details.ReleasesExclusivityKeyOnApproval(),
            _ => true,
        };
}
