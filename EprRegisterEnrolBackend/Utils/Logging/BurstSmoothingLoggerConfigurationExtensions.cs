using Serilog;
using Serilog.Configuration;

namespace EprRegisterEnrolBackend.Utils.Logging;

public static class BurstSmoothingLoggerConfigurationExtensions
{
    /// <summary>The name appsettings' <c>Serilog:WriteTo</c> uses for <see cref="SmoothBursts"/>.</summary>
    public const string SinkName = nameof(SmoothBursts);

    /// <summary>
    /// Writes to the sinks <paramref name="configure"/> sets up through a <see cref="BurstSmoothingSink"/>. Found by
    /// Serilog.Settings.Configuration through the <c>Using</c> entry in appsettings.json, the way Serilog.Sinks.Async
    /// is. The README's Logging section documents the defaults and where they come from.
    /// </summary>
    public static LoggerConfiguration SmoothBursts(
        this LoggerSinkConfiguration loggerSinkConfiguration,
        Action<LoggerSinkConfiguration> configure,
        int linesPerSecond = BurstSmoothingSettings.DefaultLinesPerSecond,
        int burst = BurstSmoothingSettings.DefaultBurst,
        int queueCapacity = BurstSmoothingSettings.DefaultQueueCapacity,
        TimeSpan? shutdownFlushTimeout = null
    )
    {
        var settings = new BurstSmoothingSettings(
            linesPerSecond,
            burst,
            queueCapacity,
            shutdownFlushTimeout ?? BurstSmoothingSettings.DefaultShutdownFlushTimeout
        );
        // Serilog calls the wrapper even when configure sets up no sink; the shipped configuration is pinned by
        // BurstSmoothingConfigurationTests.
        return loggerSinkConfiguration.Sink(
            LoggerSinkConfiguration.Wrap(inner => new BurstSmoothingSink(inner, settings, TimeProvider.System), configure)
        );
    }
}
