namespace EprRegisterEnrolBackend.Utils.Logging;

/// <summary>
/// The <see cref="BurstSmoothingSink"/>'s settings, bound from <c>Serilog:WriteTo:0:Args</c>. The defaults are the
/// ones the README's Logging section names as correct; it documents each setting and why.
/// </summary>
public sealed record BurstSmoothingSettings(
    int LinesPerSecond,
    int Burst,
    int QueueCapacity,
    TimeSpan ShutdownFlushTimeout
)
{
    // README "Logging": linesPerSecond 200, burst 50, queueCapacity 10000, shutdownFlushTimeout 00:00:05.
    public const int DefaultLinesPerSecond = 200;
    public const int DefaultBurst = 50;
    public const int DefaultQueueCapacity = 10_000;
    public static readonly TimeSpan DefaultShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    // The longest timeout Task.WaitAsync takes; anything longer would only fail at shutdown.
    private static readonly TimeSpan s_longestShutdownFlushTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Throws, naming the setting, for a value the sink can't run with.</summary>
    public void Validate()
    {
        if (LinesPerSecond < 1)
            throw Invalid("linesPerSecond", LinesPerSecond, "must be at least 1");
        if (Burst < 1)
            throw Invalid("burst", Burst, "must be at least 1");
        if (QueueCapacity < 1)
            throw Invalid("queueCapacity", QueueCapacity, "must be at least 1");
        if (Burst > QueueCapacity)
            throw Invalid("burst", Burst, $"must not be more than queueCapacity ({QueueCapacity})");
        if (ShutdownFlushTimeout <= TimeSpan.Zero || ShutdownFlushTimeout > s_longestShutdownFlushTimeout)
            throw Invalid(
                "shutdownFlushTimeout",
                ShutdownFlushTimeout,
                $"must be more than zero and at most {s_longestShutdownFlushTimeout}"
            );
    }

    private static ArgumentOutOfRangeException Invalid(string setting, object value, string rule) =>
        new(
            setting,
            value,
            $"The log burst smoothing setting {setting} (Serilog:WriteTo:{BurstSmoothingLoggerConfigurationExtensions.SinkName} Args) {rule}."
        );
}
