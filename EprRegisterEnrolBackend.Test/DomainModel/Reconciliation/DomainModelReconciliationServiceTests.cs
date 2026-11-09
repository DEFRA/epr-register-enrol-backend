using EprRegisterEnrolBackend.DomainModel.Persistence;
using EprRegisterEnrolBackend.DomainModel.Reconciliation;
using EprRegisterEnrolBackend.Test.TestSupport;
using EprRegisterEnrolBackend.Test.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.DomainModel.Reconciliation;

public class DomainModelReconciliationServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly CapturingLogger<DomainModelReconciliationService> _logger = new();

    private DomainModelReconciliationService Service(string? environment, TimeSpan? interval = null)
    {
        var schema = new DomainModelSchemaStatus();
        schema.Record(HealthCheckResult.Healthy());
        return new DomainModelReconciliationService(
            new ServiceCollection().BuildServiceProvider(),
            schema,
            new TestClock(new DateTime(2027, 1, 1, 9, 0, 0, DateTimeKind.Utc)),
            _logger
        )
        {
            CdpEnvironment = () => environment,
            Interval = interval ?? TimeSpan.FromHours(6),
        };
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    [InlineData(" prod ")]
    public async Task Start_InLive_NeverRuns(string environment)
    {
        using var service = Service(environment);

        await service.StartAsync(Ct);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var line = _logger.Entries.Should().ContainSingle().Subject;
        line.Message.Should().Contain("never runs in live");
        line.LogLevel.Should().Be(LogLevel.Warning);
    }

    private async Task WaitForLogsAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_logger.Entries.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, Ct);
        }
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("test")]
    [InlineData("ext-test")]
    public async Task Start_InATestEnvironment_TriesToRun(string environment)
    {
        using var service = Service(environment);

        await service.StartAsync(Ct);
        // The provider has no reconciler, so getting as far as resolving one is the proof it went ahead.
        await WaitForLogsAsync(1);
        await service.StopAsync(Ct);

        var line = _logger.Entries.Should().ContainSingle().Subject;
        line.LogLevel.Should().Be(LogLevel.Error);
        line.Message.Should().Contain(nameof(InvalidOperationException));
        line.Exception.Should().BeNull();
    }

    [Fact]
    public async Task Start_AFailureBeforeTheReport_NeverEscapesAndIsTriedAgain()
    {
        using var service = Service("test", TimeSpan.FromMilliseconds(10));

        await service.StartAsync(Ct);
        await WaitForLogsAsync(2);

        service
            .ExecuteTask!.IsCompleted.Should()
            .BeFalse("an escaped exception would stop the host");
        await service.StopAsync(Ct);
        _logger.Entries.Count.Should().BeGreaterThanOrEqualTo(2);
        service.ExecuteTask.IsFaulted.Should().BeFalse();
    }
}
