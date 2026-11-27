using System.Net;
using EprRegisterEnrolBackend.AccreditationApplication.Adapters;
using FluentAssertions;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Adapters;

/// <summary>RA-682: the one classification of a failed submission to ManagementBe.</summary>
public sealed class CaseWorkingSubmissionFailureTests
{
    public static TheoryData<Exception, bool> MayHoldCases() =>
        new()
        {
            { new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway), true },
            { new HttpRequestException("too many", null, HttpStatusCode.TooManyRequests), true },
            { new HttpRequestException("unreadable 2xx"), true },
            { new CaseWorkingApiTimeoutException("timed out", new TimeoutException()), true },
            { new OperationCanceledException(), true },
            { new HttpRequestException("refused", null, HttpStatusCode.BadRequest), false },
            { new HttpRequestException("conflict", null, HttpStatusCode.Conflict), false },
            { new CaseWorkingNotConfiguredException(), false },
            // A defect thrown before anything was sent, such as an update whose ORS is missing.
            { new InvalidOperationException("a defect"), false },
            { new ArgumentException("a defect"), false },
        };

    [Theory]
    [MemberData(nameof(MayHoldCases))]
    public void MayHold_is_true_only_for_a_failure_while_or_after_sending(Exception ex, bool mayHold) =>
        CaseWorkingSubmissionFailure.MayHold(ex).Should().Be(mayHold);

    public static TheoryData<Exception, bool> FailureCases() =>
        new()
        {
            { new HttpRequestException("bad gateway", null, HttpStatusCode.BadGateway), true },
            { new CaseWorkingApiTimeoutException("timed out", new TimeoutException()), true },
            { new CaseWorkingNotConfiguredException(), true },
            { new InvalidOperationException("a defect"), false },
            { new OperationCanceledException(), false },
        };

    [Theory]
    [MemberData(nameof(FailureCases))]
    public void IsCaseManagementFailure_tells_Case_Managements_failures_from_defects(Exception ex, bool isFailure) =>
        CaseWorkingSubmissionFailure.IsCaseManagementFailure(ex).Should().Be(isFailure);
}
