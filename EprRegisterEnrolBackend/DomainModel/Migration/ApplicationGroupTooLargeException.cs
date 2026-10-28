namespace EprRegisterEnrolBackend.DomainModel.Migration;

/// <summary>
/// A registration group too big to migrate in one transaction (§6.2). The write fails loudly instead of migrating
/// part of the group. The message holds the ReEx registration id and a count only.
/// </summary>
public sealed class ApplicationGroupTooLargeException(string reExRegistrationId, int limit)
    : InvalidOperationException(
        $"Registration {reExRegistrationId} has more than {limit} applications, too many to migrate in one transaction."
    )
{
    public string ReExRegistrationId { get; } = reExRegistrationId;
}
