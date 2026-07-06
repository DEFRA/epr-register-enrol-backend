using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

public class NotificationStatusResolverTests
{
    private static WorkItemAuditEntryDto Entry(
        string action,
        DateTime createdAt,
        string? templateKey = null
    ) =>
        new()
        {
            Action = action,
            CreatedAt = createdAt,
            Details = templateKey is null
                ? null
                : new Dictionary<string, string?> { ["templateKey"] = templateKey },
        };

    [Fact]
    public void Resolve_NullAuditLog_ReturnsNull()
    {
        NotificationStatusResolver.Resolve(null).Should().BeNull();
    }

    [Fact]
    public void Resolve_EmptyAuditLog_ReturnsNull()
    {
        NotificationStatusResolver.Resolve([]).Should().BeNull();
    }

    [Fact]
    public void Resolve_NoNotificationEntries_ReturnsNull()
    {
        var auditLog = new[] { Entry("work-item-submitted", DateTime.UtcNow) };
        NotificationStatusResolver.Resolve(auditLog).Should().BeNull();
    }

    [Fact]
    public void Resolve_UnresolvedFailure_ReturnsFailed()
    {
        var auditLog = new[]
        {
            Entry("notification-failed", DateTime.UtcNow, "SubmissionConfirmation"),
        };
        NotificationStatusResolver.Resolve(auditLog).Should().Be("failed");
    }

    [Fact]
    public void Resolve_LaterSameTemplateSuccess_ResolvesFailure_ReturnsSent()
    {
        var auditLog = new[]
        {
            Entry("notification-failed", DateTime.UtcNow, "SubmissionConfirmation"),
            Entry("notification-sent", DateTime.UtcNow.AddMinutes(1), "SubmissionConfirmation"),
        };
        NotificationStatusResolver.Resolve(auditLog).Should().Be("sent");
    }

    [Fact]
    public void Resolve_LaterDifferentTemplateSuccess_DoesNotResolveFailure_ReturnsFailed()
    {
        // Cross-template regression guard (ported from RA-211's JS fix): an unrelated
        // notification (e.g. DulyMade) succeeding must not mask a different one's
        // (e.g. Queried) still-unresolved failure.
        var auditLog = new[]
        {
            Entry("notification-failed", DateTime.UtcNow, "Queried"),
            Entry("notification-sent", DateTime.UtcNow.AddMinutes(1), "DulyMade"),
        };
        NotificationStatusResolver.Resolve(auditLog).Should().Be("failed");
    }

    [Fact]
    public void Resolve_EarlierSameTemplateSuccess_DoesNotResolveLaterFailure_ReturnsFailed()
    {
        var auditLog = new[]
        {
            Entry("notification-sent", DateTime.UtcNow, "SubmissionConfirmation"),
            Entry("notification-failed", DateTime.UtcNow.AddMinutes(1), "SubmissionConfirmation"),
        };
        NotificationStatusResolver.Resolve(auditLog).Should().Be("failed");
    }

    [Fact]
    public void Resolve_MissingTemplateKeyOnBothEntries_AnyLaterSuccessResolves_ReturnsSent()
    {
        var auditLog = new[]
        {
            Entry("notification-failed", DateTime.UtcNow),
            Entry("notification-sent", DateTime.UtcNow.AddMinutes(1)),
        };
        NotificationStatusResolver.Resolve(auditLog).Should().Be("sent");
    }

    [Fact]
    public void Resolve_OnlySuccesses_ReturnsSent()
    {
        var auditLog = new[]
        {
            Entry("notification-sent", DateTime.UtcNow, "SubmissionConfirmation"),
        };
        NotificationStatusResolver.Resolve(auditLog).Should().Be("sent");
    }

    [Fact]
    public void Resolve_OneResolvedOneUnresolvedFailure_ReturnsFailed()
    {
        var auditLog = new[]
        {
            Entry("notification-failed", DateTime.UtcNow, "SubmissionConfirmation"),
            Entry("notification-sent", DateTime.UtcNow.AddMinutes(1), "SubmissionConfirmation"),
            Entry("notification-failed", DateTime.UtcNow.AddMinutes(2), "Queried"),
        };
        NotificationStatusResolver.Resolve(auditLog).Should().Be("failed");
    }
}
