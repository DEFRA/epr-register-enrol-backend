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

    private DomainModelReconciliationService Service(string? environment)
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
        };
    }

    [Theory]
    [InlineData("prod")]
    [InlineData("PROD")]
    public async Task Start_InLive_NeverRuns(string environment)
    {
        using var service = Service(environment);

        await service.StartAsync(Ct);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var line = _logger.Entries.Should().ContainSingle().Subject;
        line.Message.Should().Contain("never runs in live");
        line.LogLevel.Should().Be(LogLevel.Warning);
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
        var failure = () => service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        await failure.Should().ThrowAsync<InvalidOperationException>();
        _logger.Entries.Should().NotContain(e => e.Message.Contains("never runs in live"));
    }
}
