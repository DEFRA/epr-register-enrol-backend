using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

public class RegulatoryNumberBackfillHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_Unhealthy_WhenBackfillNotYetComplete()
    {
        var status = new RegulatoryNumberBackfillStatus();
        var check = new RegulatoryNumberBackfillHealthCheck(status);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task CheckHealthAsync_Healthy_WhenBackfillComplete()
    {
        var status = new RegulatoryNumberBackfillStatus();
        status.MarkComplete();
        var check = new RegulatoryNumberBackfillHealthCheck(status);

        var result = await check.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.Current.CancellationToken
        );

        result.Status.Should().Be(HealthStatus.Healthy);
    }
}
