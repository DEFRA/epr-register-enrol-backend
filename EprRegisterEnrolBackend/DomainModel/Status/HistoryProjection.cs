using EprRegisterEnrolBackend.DomainModel.Versioning;

namespace EprRegisterEnrolBackend.DomainModel.Status;

internal static class HistoryProjection
{
    /// <summary>
    /// One entry per version, in version order, where the selected value differs from the last one emitted. A
    /// version with no value (null) is skipped and does not reset the last value, so a value that disappears and
    /// returns unchanged is not repeated.
    /// </summary>
    public static List<TEntry> Changes<TVersion, TValue, TEntry>(
        IEnumerable<TVersion> versions,
        Func<TVersion, TValue?> select,
        Func<TVersion, TValue, TEntry> toEntry
    )
        where TVersion : VersionedEntityBase
        where TValue : struct
    {
        var entries = new List<TEntry>();
        TValue? last = null;

        foreach (var version in versions.OrderBy(v => v.Version))
        {
            var value = select(version);
            if (value is null || (last is not null && last.Value.Equals(value.Value)))
            {
                continue;
            }

            entries.Add(toEntry(version, value.Value));
            last = value;
        }

        return entries;
    }
}
