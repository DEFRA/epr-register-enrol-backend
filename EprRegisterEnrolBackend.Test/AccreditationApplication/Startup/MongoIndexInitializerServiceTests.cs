using EprRegisterEnrolBackend.AccreditationApplication.Services;
using EprRegisterEnrolBackend.AccreditationApplication.Startup;
using EprRegisterEnrolBackend.Auth;
using EprRegisterEnrolBackend.CdpUploader.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.AccreditationApplication.Startup;

public class MongoIndexInitializerServiceTests
{
    private static MongoIndexInitializerService CreateSut(
        IServiceProvider serviceProvider,
        ILogger<MongoIndexInitializerService> logger
    ) => new(serviceProvider, logger);

    private static IServiceProvider AllPersistencesRegistered() =>
        new ServiceCollection()
            .AddSingleton(Substitute.For<IAccreditationApplicationPersistence>())
            .AddSingleton(Substitute.For<IRecyclingOperationsAuditPersistence>())
            .AddSingleton(Substitute.For<IPendingUploadService>())
            .AddSingleton(Substitute.For<ICaseManagementAuthNonceStore>())
            .BuildServiceProvider();

    [Fact]
    public async Task InitializeAsync_resolves_the_mongo_backed_persistences()
    {
        var logger = new CapturingLogger();
        var provider = AllPersistencesRegistered();

        var act = () => CreateSut(provider, logger).InitializeAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        // GetRequiredService for any of the four registered persistences failing would be
        // caught and logged as an Error instead of escaping — assert success explicitly so
        // this test cannot pass merely because the catch block swallowed a resolution failure.
        logger.Entries.Should().NotContain(e => e.LogLevel == LogLevel.Error);
        logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Information);
    }

    [Fact]
    public async Task InitializeAsync_swallows_a_resolution_failure()
    {
        // No persistences registered → GetRequiredService throws. The service
        // must log and move on rather than let the exception escape (which
        // would crash-loop the host from a BackgroundService).
        var logger = new CapturingLogger();
        var provider = new ServiceCollection().BuildServiceProvider();

        var act = () => CreateSut(provider, logger).InitializeAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        logger.Entries.Should().ContainSingle(e => e.LogLevel == LogLevel.Error);
    }

    [Fact]
    public async Task InitializeAsync_returns_quietly_when_cancelled()
    {
        var provider = AllPersistencesRegistered();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => CreateSut(provider, new CapturingLogger()).InitializeAsync(cts.Token);

        await act.Should().NotThrowAsync();
    }

    private sealed class CapturingLogger : ILogger<MongoIndexInitializerService>
    {
        public List<(LogLevel LogLevel, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
