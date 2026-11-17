using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.DomainModel.Entities;
using MongoDB.Bson;

namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>
/// The v2 API's <c>statusHistory[]</c>, projected from an Accreditation's versions (design §4.4, Q21): one entry
/// per version whose status changed. Status is recorded time only, so an entry applies from the later of 1 January
/// of the accreditation year and the UK date it was recorded, and to the day before the next entry applies from
/// (see <see cref="AccreditationApiMapper.BuildHistory"/>).
/// </summary>
public static class StatusHistoryProjection
{
    public static IReadOnlyList<StatusHistoryEntryDto> ForAccreditation(
        IEnumerable<AccreditationEntity> versions
    )
    {
        var all = versions.ToList();
        return Build(
            all,
            HistoryProjection.Changes(
                all,
                v => (AccreditationStatus?)v.Status,
                (v, status) => Change(v, status.ToString())
            )
        );
    }

    /// <summary>
    /// One accredited overseas site's status within the Accreditation. Versions that do not include the site are
    /// skipped.
    /// </summary>
    public static IReadOnlyList<StatusHistoryEntryDto> ForAccreditedSite(
        IEnumerable<AccreditationEntity> versions,
        ObjectId overseasSiteId
    )
    {
        var all = versions.ToList();
        return Build(
            all,
            HistoryProjection.Changes(
                all,
                v =>
                    v.AccreditationOverseasSites.FirstOrDefault(s =>
                        s.OverseasSiteId == overseasSiteId
                    )?.Status,
                (v, status) => Change(v, status.ToString())
            )
        );
    }

    private static (string Status, DateTime RecordedAt) Change(
        AccreditationEntity version,
        string status
    ) => (status.ToLowerInvariant(), version.ValidFrom);

    private static IReadOnlyList<StatusHistoryEntryDto> Build(
        List<AccreditationEntity> versions,
        List<(string Status, DateTime RecordedAt)> changes
    ) =>
        changes.Count == 0
            ? []
            : AccreditationApiMapper.BuildHistory(changes, versions[0].Year);
}
