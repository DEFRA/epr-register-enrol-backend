namespace EprRegisterEnrolBackend.Utils.Logging;

/// <summary>
/// The <see cref="BurstSmoothingSink"/>'s settings, bound from <c>Serilog:WriteTo:0:Args</c>. The README's Logging
/// section documents each setting, its default and where the default comes from.
/// </summary>
public sealed record BurstSmoothingSettings(
    int LinesPerSecond,
    int Burst,
    int QueueCapacity,
    TimeSpan ShutdownFlushTimeout
)
{
    public const int DefaultLinesPerSecond = 200;
    public const int DefaultBurst = 50;
    public const int DefaultQueueCapacity = 10_000;
    public static readonly TimeSpan DefaultShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    // Disposal waits up to this plus StopGrace (1 s), and ECS kills the task 30 s after asking it to stop, after the
    // hosted services have had their turn. Also catches a bare number: Serilog.Settings.Configuration reads
    // shutdownFlushTimeout with TimeSpan.Parse, so "5" means 5 days.
    public static readonly TimeSpan LongestShutdownFlushTimeout = TimeSpan.FromSeconds(20);

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
        if (ShutdownFlushTimeout <= TimeSpan.Zero || ShutdownFlushTimeout > LongestShutdownFlushTimeout)
            throw Invalid(
                "shutdownFlushTimeout",
                ShutdownFlushTimeout,
                $"must be more than zero and at most {LongestShutdownFlushTimeout}"
            );
    }

    // Names the key as the README tells people to set it: the shipped config has one WriteTo entry, this sink.
    private static ArgumentOutOfRangeException Invalid(string setting, object value, string rule) =>
        new(
            setting,
            value,
            $"The log burst smoothing setting {setting} (Serilog:WriteTo:0:Args:{setting}, or the environment "
                + $"variable Serilog__WriteTo__0__Args__{setting}) {rule}."
        );
}
