using EprRegisterEnrolBackend.AccreditationApplication.Models;

namespace EprRegisterEnrolBackend.AccreditationApplication.Services;

/// <summary>
/// RA-781: the status changes stored on an interim site. An interim site is not approved as such: it
/// is <c>approved</c> when it is added, <c>withdrawn</c> each time it is withdrawn and <c>approved</c>
/// again each time it is restored, so restoring it does not lose the withdrawal. <see cref="InterimSiteModel.RemovedAt"/>
/// stays what the rest of the application reads; every change to it goes through here so the two agree.
///
/// A document written before this field has no history, and nothing can recover the withdrawals a
/// restore already cleared. <see cref="Seed"/> gives such a site the history it can know (approved, then
/// withdrawn if it is withdrawn now) before the first change is added to it.
/// </summary>
public static class InterimSiteStatusHistory
{
    public const string Approved = "approved";
    public const string Withdrawn = "withdrawn";

    /// <summary>The history of an interim site that has just been added.</summary>
    public static List<InterimSiteStatusChange> Added(DateTime at) =>
        [new() { Status = Approved, RecordedAt = at }];

    /// <summary>
    /// Withdraws the site: a <c>withdrawn</c> entry and <c>RemovedAt</c>. Idempotent, and it never
    /// overwrites an earlier withdrawal date.
    /// </summary>
    public static void Withdraw(InterimSiteModel interim, DateTime at)
    {
        if (interim.RemovedAt is not null)
            return;

        Seed(interim, at);
        interim.StatusHistory.Add(new InterimSiteStatusChange { Status = Withdrawn, RecordedAt = at });
        interim.RemovedAt = at;
    }

    /// <summary>
    /// Restores the site: an <c>approved</c> entry, and <c>RemovedAt</c> cleared. A site that is not
    /// withdrawn has nothing to restore.
    /// </summary>
    public static void Restore(InterimSiteModel interim, DateTime at)
    {
        if (interim.RemovedAt is null)
            return;

        Seed(interim, at);
        interim.StatusHistory.Add(new InterimSiteStatusChange { Status = Approved, RecordedAt = at });
        interim.RemovedAt = null;
    }

    /// <summary>
    /// Gives a site with no stored history the one it can know: approved from when it was added (when
    /// that was not recorded, the nearest date there is), then withdrawn if it is withdrawn now.
    /// </summary>
    public static void Seed(InterimSiteModel interim, DateTime at)
    {
        if (interim.StatusHistory.Count > 0)
            return;

        interim.StatusHistory.Add(
            new InterimSiteStatusChange
            {
                Status = Approved,
                RecordedAt = interim.CreatedAt ?? interim.RemovedAt ?? at,
            }
        );
        if (interim.RemovedAt is { } removed)
            interim.StatusHistory.Add(new InterimSiteStatusChange { Status = Withdrawn, RecordedAt = removed });
    }
}
