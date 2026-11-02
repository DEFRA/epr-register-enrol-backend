using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.DomainModel.Entities;

namespace EprRegisterEnrolBackend.DomainModel.Status;

/// <summary>
/// The v2 API's <c>tonnageBandHistory[]</c>, projected from an Accreditation's versions (design §4.4, D2): one
/// entry per version whose band changed, recorded time only.
/// </summary>
public static class TonnageBandHistoryProjection
{
    public static IReadOnlyList<TonnageBandHistoryEntryDto> ForAccreditation(
        IEnumerable<AccreditationEntity> versions
    ) =>
        HistoryProjection.Changes(
            versions,
            v => v.TonnageBand,
            (v, band) =>
                new TonnageBandHistoryEntryDto
                {
                    TonnageBand = TonnageBandConversion.ToReExName(band),
                    RecordedAt = v.ValidFrom,
                }
        );
}
