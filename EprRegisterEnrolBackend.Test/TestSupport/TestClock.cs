namespace EprRegisterEnrolBackend.Test.TestSupport;

/// <summary>A <see cref="TimeProvider"/> tests move by hand.</summary>
public sealed class TestClock(DateTime start) : TimeProvider
{
    private DateTimeOffset _now = new(start, TimeSpan.Zero);

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}
