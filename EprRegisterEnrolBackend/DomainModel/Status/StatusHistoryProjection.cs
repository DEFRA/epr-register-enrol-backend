using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.DomainModel.Entities;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>
/// The v2 API's <c>statusHistory[]</c>, projected from an Accreditation's versions (design §4.4, Q21): one entry
/// per version whose status changed. Status is recorded time only, so an entry takes effect on the UK date it
/// was recorded.
/// </summary>
public static class StatusHistoryProjection
{
    public static IReadOnlyList<StatusHistoryEntryDto> ForAccreditation(
        IEnumerable<AccreditationEntity> versions
    ) =>
        HistoryProjection.Changes(
            versions,
            v => (AccreditationStatus?)v.Status,
            (v, status) => Entry(v, status.ToString())
        );

    /// <summary>
    /// One accredited overseas site's status within the Accreditation. Versions that do not include the site are
    /// skipped.
    /// </summary>
    public static IReadOnlyList<StatusHistoryEntryDto> ForAccreditedSite(
        IEnumerable<AccreditationEntity> versions,
        ObjectId overseasSiteId
    ) =>
        HistoryProjection.Changes(
            versions,
            v =>
                v.AccreditationOverseasSites.FirstOrDefault(s =>
                    s.OverseasSiteId == overseasSiteId
                )?.Status,
            (v, status) => Entry(v, status.ToString())
        );

    private static StatusHistoryEntryDto Entry(AccreditationEntity version, string status) =>
        new()
        {
            Status = status.ToLowerInvariant(),
            EffectiveDate = UkCalendar.ToUkDate(version.ValidFrom),
            RecordedAt = version.ValidFrom,
        };
}
