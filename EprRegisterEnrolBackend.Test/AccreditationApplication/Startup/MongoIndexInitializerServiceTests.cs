using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

public class MongoIndexInitializerServiceTests
{
    private static MongoIndexInitializerService CreateSut(IServiceProvider serviceProvider) =>
        new(serviceProvider, NullLogger<MongoIndexInitializerService>.Instance);

    [Fact]
    public async Task InitializeAsync_resolves_the_mongo_backed_persistences()
    {
        var accreditation = Substitute.For<IAccreditationApplicationPersistence>();
        var audit = Substitute.For<IRecyclingOperationsAuditPersistence>();
        var provider = new ServiceCollection()
            .AddSingleton(accreditation)
            .AddSingleton(audit)
            .BuildServiceProvider();

        var act = () => CreateSut(provider).InitializeAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InitializeAsync_swallows_a_resolution_failure()
    {
        // No persistences registered → GetRequiredService throws. The service
        // must log and move on rather than let the exception escape (which
        // would crash-loop the host from a BackgroundService).
        var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => CreateSut(provider).InitializeAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task InitializeAsync_returns_quietly_when_cancelled()
    {
        var provider = new ServiceCollection()
            .AddSingleton(Substitute.For<IAccreditationApplicationPersistence>())
            .AddSingleton(Substitute.For<IRecyclingOperationsAuditPersistence>())
            .BuildServiceProvider();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CreateSut(provider).InitializeAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }
}
