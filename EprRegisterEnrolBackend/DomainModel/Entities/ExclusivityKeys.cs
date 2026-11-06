using EprRegisterEnrolBackend.AccreditationApplication.Models;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Entities;

/// <summary>
/// The <see cref="ApplicationEntity.ExclusivityKey"/> each application type holds while it is open, in the formats and
/// with the release rules of register-enrol-application-types.md ("Rules and indexes"). Application code chooses the
/// rule by type; the partial unique index on the key only catches two writers that both passed the check at once.
/// Nothing writes the key yet: writing it on accreditation applications would refuse a second live application that
/// legacy allows today, so it waits for the one-live-application rule (design doc §7 R12).
/// </summary>
public static class ExclusivityKeys
{
    /// <summary>The key the application should hold now: its key while it holds one, otherwise null.</summary>
    public static string? For(ApplicationEntity application) =>
        HoldsKey(application.ApplicationType, application.Status) ? Build(application) : null;

    /// <summary>
    /// The application's key, whatever its status, or null when its type has no key or a part is missing. A missing
    /// part never builds a key, or every application missing it would share one and collide.
    /// </summary>
    public static string? Build(ApplicationEntity application) =>
        application.Details switch
        {
            AccreditationDetails details when application.RegistrationId is { } registrationId =>
                $"accreditation|{registrationId}|{details.Year}",
            OrsChangeDetails details when details.OverseasSiteId != ObjectId.Empty =>
                $"ors-change|{details.OverseasSiteId}",
            TonnageChangeDetails details when details.TargetAccreditationId != ObjectId.Empty =>
                $"tonnage-change|{details.TargetAccreditationId}",
            AnnualChargeDetails details when application.RegistrationId is { } registrationId =>
                $"annual-charge|{registrationId}|{details.ChargeYear}",
            // New Registration has no key until its journey decides how a site is identified.
            _ => null,
        };

    /// <summary>
    /// Whether an application of <paramref name="type"/> at <paramref name="status"/> still holds its key. Every type
    /// releases it when rejected or withdrawn. In-year changes also release it on approval, because the next change to
    /// the same ORS or Accreditation is a new application. The others keep it once approved: an accreditation
    /// application, because a cancelled Accreditation is reinstated as a new version of itself, never replaced (Q36).
    /// </summary>
    public static bool HoldsKey(ApplicationType type, ApplicationStatus status) =>
        status switch
        {
            ApplicationStatus.Rejected or ApplicationStatus.Withdrawn => false,
            ApplicationStatus.Approved => type
                is not (ApplicationType.OrsChange or ApplicationType.TonnageChange),
            _ => true,
        };
}
