using EprRegisterEnrolBackend.AccreditationApi;
using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

// RA-651: the check that tells operators about approved applications the API cannot serve. The
// log line is what the existing "5xx and Errors" alert reads, so its level is part of the contract.
public class UnservableAccreditationCheckServiceTests
{
    private readonly IAccreditationApplicationPersistence _persistence =
        Substitute.For<IAccreditationApplicationPersistence>();
    private readonly CapturingLoggerFactory _logs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly UnservableApprovedCounts None = new(0, 0, 0);

    private void CountReturns(int firstYear, UnservableApprovedCounts counts) =>
        _persistence
            .CountUnservableApprovedAsync(firstYear, Arg.Any<CancellationToken>())
            .Returns(counts);

    private UnservableAccreditationCheckService Service(
        AccreditationApiMonitoringConfig? monitoring = null,
        int firstYear = 2027
    ) =>
        new(
            _persistence,
            Options.Create(new ServiceClientsConfig { FirstAccreditationYear = firstYear }),
            Options.Create(monitoring ?? new AccreditationApiMonitoringConfig()),
            _logs.CreateLogger<UnservableAccreditationCheckService>()
        );

    [Fact]
    public async Task RunOnce_WhenEveryRecordCanBeServed_LogsInformation_NotAnError()
    {
        CountReturns(2027, None);

        await Service().RunOnceAsync(Ct);

        var entry = _logs.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Information);
        entry.Properties["FromYear"].Should().Be(2027);
    }

    [Fact]
    public async Task RunOnce_WhenSomeCannotBeServed_LogsOneErrorWithTheCounts()
    {
        CountReturns(2028, new UnservableApprovedCounts(1, 2, 3));

        await Service(firstYear: 2028).RunOnceAsync(Ct);

        var entry = _logs.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Error);
        entry.Message.Should().Be(
            "Approved accreditations the accreditation API cannot serve: noOrganisationNumber=1 noRegistrationNumber=2 noAccreditationNumber=3 fromYear=2028"
        );
        // The fields a dashboard or alert filters on.
        entry.Properties["NoOrganisationNumber"].Should().Be(1);
        entry.Properties["NoRegistrationNumber"].Should().Be(2);
        entry.Properties["NoAccreditationNumber"].Should().Be(3);
        entry.Properties["FromYear"].Should().Be(2028);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 0)]
    [InlineData(0, 0, 1)]
    public async Task RunOnce_AnySingleReason_IsAnError(int organisation, int registration, int accreditation)
    {
        CountReturns(2027, new UnservableApprovedCounts(organisation, registration, accreditation));

        await Service().RunOnceAsync(Ct);

        _logs.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task RunOnce_WhenTheCountFails_LogsAWarning_AndDoesNotThrow()
    {
        _persistence
            .CountUnservableApprovedAsync(2027, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("mongo is down"));

        var act = () => Service().RunOnceAsync(Ct);

        await act.Should().NotThrowAsync();
        var entry = _logs.Entries.Should().ContainSingle().Subject;
        entry.LogLevel.Should().Be(LogLevel.Warning);
        entry.Message.Should().StartWith("Checking for approved accreditations");
    }

    // The reviewer's point: a count still running at shutdown must be cancellable.
    [Fact]
    public async Task RunOnce_PassesItsTokenToTheCount()
    {
        CountReturns(2027, None);
        using var cts = new CancellationTokenSource();

        await Service().RunOnceAsync(cts.Token);

        await _persistence.Received(1).CountUnservableApprovedAsync(2027, cts.Token);
    }

    [Fact]
    public async Task RunOnce_WhenCancelledMidCount_LogsNothing()
    {
        using var cts = new CancellationTokenSource();
        _persistence
            .CountUnservableApprovedAsync(2027, Arg.Any<CancellationToken>())
            .Returns<Task<UnservableApprovedCounts>>(_ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            });

        var act = () => Service().RunOnceAsync(cts.Token);

        await act.Should().NotThrowAsync();
        _logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenSwitchedOff_NeverTouchesTheDatabase_EvenWithNoDelay()
    {
        // With no delay and a short interval, a service that ignored Enabled would count within
        // milliseconds, so this fails if the flag is not honoured.
        var service = Service(
            new AccreditationApiMonitoringConfig
            {
                Enabled = false,
                InitialDelay = TimeSpan.Zero,
                Interval = TimeSpan.FromMilliseconds(10),
            }
        );

        await service.StartAsync(Ct);
        await Task.Delay(200, Ct);
        await service.StopAsync(Ct);

        await _persistence
            .DidNotReceiveWithAnyArgs()
            .CountUnservableApprovedAsync(default, default);
        _logs.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Once_Started_ChecksAgainOnEveryInterval_AndStopsCleanly()
    {
        CountReturns(2027, None);
        var service = Service(
            new AccreditationApiMonitoringConfig
            {
                InitialDelay = TimeSpan.Zero,
                Interval = TimeSpan.FromMilliseconds(20),
            }
        );

        await service.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_logs.Entries.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);
        var stopped = await Record.ExceptionAsync(() => service.StopAsync(Ct));

        stopped.Should().BeNull();
        _logs.Entries.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    // The reviewer's point: an exception out of a hosted service stops the whole backend, and a
    // mistyped setting makes Task.Delay or PeriodicTimer throw. A bad value is replaced instead.
    public static IEnumerable<object[]> OutOfRangeSettings() =>
        [
            ["InitialDelay", TimeSpan.FromSeconds(-1), TimeSpan.FromDays(1)],
            ["InitialDelay", TimeSpan.FromDays(2), TimeSpan.FromDays(1)],
            ["Interval", TimeSpan.FromHours(1), TimeSpan.Zero],
            ["Interval", TimeSpan.FromHours(1), TimeSpan.FromSeconds(-1)],
            ["Interval", TimeSpan.FromHours(1), TimeSpan.FromDays(31)],
        ];

    [Theory]
    [MemberData(nameof(OutOfRangeSettings))]
    public async Task OutOfRangeSettings_AreIgnoredWithAWarning_AndDoNotStopTheHost(
        string setting,
        TimeSpan initialDelay,
        TimeSpan interval
    )
    {
        var service = Service(
            new AccreditationApiMonitoringConfig { InitialDelay = initialDelay, Interval = interval }
        );

        await service.StartAsync(Ct);
        // ExecuteAsync may not have run yet when StartAsync returns, so wait for its warning.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_logs.Entries.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);
        var stopped = await Record.ExceptionAsync(() => service.StopAsync(Ct));

        stopped.Should().BeNull();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
        var warning = _logs.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Warning).Subject;
        warning.Properties["Setting"].Should().Be(setting);
    }

    [Fact]
    public async Task SettingsInRange_AreUsedAsTheyAre_WithoutAWarning()
    {
        CountReturns(2027, None);
        var service = Service(
            new AccreditationApiMonitoringConfig
            {
                InitialDelay = TimeSpan.Zero,
                Interval = TimeSpan.FromMilliseconds(20),
            }
        );

        await service.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_logs.Entries.Count < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);
        await service.StopAsync(Ct);

        _logs.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Warning);
    }
}
