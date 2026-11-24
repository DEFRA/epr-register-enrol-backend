using EprRegisterEnrolBackend.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Parsing;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

public class BurstSmoothingSinkTests
{
    private static readonly MessageTemplateParser s_parser = new();

    // Fast enough that a test which isn't about the rate never waits on it.
    private static BurstSmoothingSettings Unthrottled(int queueCapacity = 10_000) =>
        new(1_000_000, queueCapacity, queueCapacity, TimeSpan.FromSeconds(30));

    [Fact]
    public async Task Lines_up_to_the_burst_go_out_at_once_then_at_the_sustained_rate()
    {
        var clock = new SignallingClock();
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, new(10, 5, 100, TimeSpan.FromSeconds(5)), clock);

        for (var i = 0; i < 20; i++)
            sink.Emit(Line($"line {i}"));

        await clock.WaitForTimerAsync();
        inner.Messages.Should().HaveCount(5, "the burst goes out at once, and no more");

        // 10 a second: one line every 100 ms, and none in between.
        for (var step = 1; step <= 10; step++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await clock.WaitForTimerAsync();
            inner.Messages.Should().HaveCount(5 + step);
        }

        clock.Advance(TimeSpan.FromMilliseconds(99));
        inner.Messages.Should().HaveCount(15, "the next line is not due for another millisecond");

        await DisposeAdvancingAsync(sink, clock);
        inner.Messages.Should().Equal(Enumerable.Range(0, 20).Select(i => $"line {i}"));
    }

    [Fact]
    public async Task An_idle_spell_refills_the_burst_but_no_more()
    {
        var clock = new SignallingClock();
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, new(10, 5, 100, TimeSpan.FromSeconds(5)), clock);

        clock.Advance(TimeSpan.FromDays(1000));
        for (var i = 0; i < 8; i++)
            sink.Emit(Line($"line {i}"));

        await clock.WaitForTimerAsync();
        inner.Messages.Should().HaveCount(5);

        await DisposeAdvancingAsync(sink, clock);
    }

    [Fact]
    public async Task Lines_keep_their_order()
    {
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, Unthrottled(), TimeProvider.System);

        for (var i = 0; i < 5_000; i++)
            sink.Emit(Line($"line {i}"));
        await sink.DisposeAsync();

        inner.Messages.Should().Equal(Enumerable.Range(0, 5_000).Select(i => $"line {i}"));
    }

    [Fact]
    public async Task A_full_queue_never_holds_up_the_caller_and_its_dropped_lines_are_reported_at_error_before_the_next_line()
    {
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, Unthrottled(queueCapacity: 3), TimeProvider.System);
        inner.BlockOn("held");

        sink.Emit(Line("held"));
        await inner.Blocked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // The writer is stuck on "held", so 3 lines fill the queue and the other 7 are dropped, without waiting.
        for (var i = 1; i <= 10; i++)
            sink.Emit(Line($"line {i}"));
        inner.Release();
        await sink.DisposeAsync();

        inner.Messages.Should().Equal(
            "held",
            "Dropped 7 log lines because the log queue was full",
            "line 1",
            "line 2",
            "line 3"
        );
        var dropLine = inner.Events[1];
        dropLine.Level.Should().Be(LogEventLevel.Error);
        dropLine.Properties["SourceContext"]
            .Should()
            .Be(new ScalarValue("EprRegisterEnrolBackend.Utils.Logging.BurstSmoothingSink"));
        dropLine.Properties["DroppedLogLines"].Should().Be(new ScalarValue(7L));
    }

    [Fact]
    public async Task Disposal_flushes_the_queue_and_then_disposes_the_wrapped_sink()
    {
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, Unthrottled(), TimeProvider.System);

        for (var i = 0; i < 100; i++)
            sink.Emit(Line($"line {i}"));
        await sink.DisposeAsync();

        inner.Messages.Should().HaveCount(100).And.OnlyContain(m => m.StartsWith("line "));
        inner.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Disposing_synchronously_flushes_once_and_later_lines_do_not_throw()
    {
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, Unthrottled(), TimeProvider.System);

        sink.Emit(Line("line 0"));
        sink.Dispose();
        sink.Dispose();
        sink.Emit(Line("too late"));

        inner.Messages.Should().Equal("line 0");
        inner.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task Disposal_reports_the_lines_it_could_not_flush_in_time_in_one_final_error()
    {
        var clock = new SignallingClock();
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, new(1, 1, 5, TimeSpan.FromSeconds(4.5)), clock);

        sink.Emit(Line("line 0"));
        await inner.WaitForCountAsync(1);
        sink.Emit(Line("line 1"));
        await clock.WaitForTimerAsync(); // The writer holds line 1, waiting for its turn.
        for (var i = 2; i <= 9; i++)
            sink.Emit(Line($"line {i}")); // 2 to 6 are queued; 7, 8 and 9 are dropped.

        var disposal = sink.DisposeAsync().AsTask();
        await clock.WaitForTimerAsync(); // The flush timeout.
        for (var second = 1; second <= 4; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.WaitForTimerAsync();
        }
        clock.Advance(TimeSpan.FromSeconds(0.5));
        await disposal;

        inner.Messages.Should().Equal(
            "line 0",
            "Dropped 3 log lines because the log queue was full",
            "line 1",
            "line 2",
            "line 3",
            "Dropped 3 log lines at shutdown: 3 were still queued after 00:00:04.5000000 and 0 were refused "
                + "because the log queue was full or closed"
        );
        inner.Events[^1].Level.Should().Be(LogEventLevel.Error);
        inner.Events[^1].Properties["SourceContext"]
            .Should()
            .Be(new ScalarValue(BurstSmoothingSink.SourceContext));
        inner.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task The_final_error_also_counts_drops_not_yet_reported()
    {
        var clock = new SignallingClock();
        var inner = new RecordingSink();
        var sink = new BurstSmoothingSink(inner, new(1, 1, 5, TimeSpan.FromSeconds(0.5)), clock);

        sink.Emit(Line("line 0"));
        await inner.WaitForCountAsync(1);
        sink.Emit(Line("line 1"));
        await clock.WaitForTimerAsync();
        for (var i = 2; i <= 9; i++)
            sink.Emit(Line($"line {i}"));

        var disposal = sink.DisposeAsync().AsTask();
        await clock.WaitForTimerAsync();
        clock.Advance(TimeSpan.FromSeconds(0.5));
        await disposal;

        inner.Messages.Should().Equal(
            "line 0",
            "Dropped 9 log lines at shutdown: 6 were still queued after 00:00:00.5000000 and 3 were refused "
                + "because the log queue was full or closed"
        );
    }

    [Fact]
    public async Task A_failing_wrapped_sink_does_not_stop_logging()
    {
        var inner = new RecordingSink();
        inner.ThrowOn("boom");
        var sink = new BurstSmoothingSink(inner, Unthrottled(), TimeProvider.System);

        sink.Emit(Line("before"));
        sink.Emit(Line("boom"));
        sink.Emit(Line("after"));
        await sink.DisposeAsync();

        inner.Messages.Should().Equal("before", "after");
    }

    [Theory]
    [InlineData(0, 1, 1, 1_000, "linesPerSecond")]
    [InlineData(-5, 1, 1, 1_000, "linesPerSecond")]
    [InlineData(1, 0, 1, 1_000, "burst")]
    [InlineData(1, 1, 0, 1_000, "queueCapacity")]
    [InlineData(1, 11, 10, 1_000, "burst")]
    [InlineData(1, 1, 1, 0, "shutdownFlushTimeout")]
    [InlineData(1, 1, 1, -1, "shutdownFlushTimeout")]
    [InlineData(1, 1, 1, 4_294_967_295, "shutdownFlushTimeout")]
    public void Invalid_settings_throw_naming_the_setting(
        int linesPerSecond,
        int burst,
        int queueCapacity,
        long timeoutMs,
        string setting
    )
    {
        var settings = new BurstSmoothingSettings(
            linesPerSecond,
            burst,
            queueCapacity,
            TimeSpan.FromMilliseconds(timeoutMs)
        );

        var construct = () => new BurstSmoothingSink(new RecordingSink(), settings, TimeProvider.System);

        construct
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .Where(e => e.ParamName == setting)
            .WithMessage($"The log burst smoothing setting {setting} (Serilog:WriteTo:SmoothBursts Args) *");
    }

    [Fact]
    public void Smoothing_wraps_the_configured_sink_and_disposes_it_with_the_logger()
    {
        var inner = new RecordingSink();
        var logger = new LoggerConfiguration()
            .WriteTo.SmoothBursts(w => w.Sink(inner))
            .CreateLogger();

        logger.Warning("through the smoother {Number}", 1);
        logger.Dispose();

        inner.Messages.Should().Equal("through the smoother 1");
        inner.Disposed.Should().BeTrue();
    }

    internal static LogEvent Line(string text) =>
        new(DateTimeOffset.UtcNow, LogEventLevel.Information, null, s_parser.Parse(text), []);

    // Disposal flushes at the sink's rate, so on a fake clock time has to be moved on until it finishes.
    private static async Task DisposeAdvancingAsync(BurstSmoothingSink sink, FakeTimeProvider clock)
    {
        var disposal = sink.DisposeAsync().AsTask();
        for (var i = 0; i < 1_000 && !disposal.IsCompleted; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        await disposal;
    }

    /// <summary>A fake clock that says when something starts waiting on it, so a test advances it only then.</summary>
    internal sealed class SignallingClock : FakeTimeProvider
    {
        private readonly SemaphoreSlim _timers = new(0);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period
        )
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            _timers.Release();
            return timer;
        }

        public async Task WaitForTimerAsync()
        {
            var started = await _timers.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            started.Should().BeTrue("something should be waiting on the clock");
        }
    }

    internal sealed class RecordingSink : ILogEventSink, IDisposable
    {
        private readonly Lock _lock = new();
        private readonly List<LogEvent> _events = [];
        private readonly ManualResetEventSlim _released = new(true);
        private string? _blockOn;
        private string? _throwOn;

        public SemaphoreSlim Blocked { get; } = new(0);
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount > 0;

        public IReadOnlyList<LogEvent> Events
        {
            get
            {
                lock (_lock)
                    return [.. _events];
            }
        }

        public IReadOnlyList<string> Messages => [.. Events.Select(e => e.RenderMessage())];

        public void BlockOn(string message)
        {
            _blockOn = message;
            _released.Reset();
        }

        public void Release() => _released.Set();

        public void ThrowOn(string message) => _throwOn = message;

        public void Emit(LogEvent logEvent)
        {
            var message = logEvent.RenderMessage();
            if (message == _throwOn)
                throw new InvalidOperationException("the wrapped sink failed");
            if (message == _blockOn)
            {
                Blocked.Release();
                _released.Wait(TimeSpan.FromSeconds(10));
            }

            lock (_lock)
                _events.Add(logEvent);
        }

        public async Task WaitForCountAsync(int count)
        {
            for (var i = 0; i < 1_000 && Events.Count < count; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Events.Should().HaveCount(count);
        }

        public void Dispose() => DisposeCount++;
    }
}

// SelfLog is process-wide, so this runs with nothing alongside.
[Collection(nameof(ProcessWideLogging))]
public class BurstSmoothingSinkStuckWriterTests
{
    // The wrapped sink blocks in Emit (say, stdout has stopped draining), so cancelling the reader can't stop it.
    // Disposal still returns after the flush timeout plus StopGrace, and the report goes to SelfLog.
    [Fact]
    public async Task Disposal_gives_up_on_a_stuck_wrapped_sink_and_reports_through_self_log()
    {
        var clock = new BurstSmoothingSinkTests.SignallingClock();
        var inner = new BurstSmoothingSinkTests.RecordingSink();
        var sink = new BurstSmoothingSink(inner, new(1_000, 10, 10, TimeSpan.FromSeconds(2)), clock);
        using var selfLog = new StringWriter();
        SelfLog.Enable(TextWriter.Synchronized(selfLog));
        try
        {
            inner.BlockOn("stuck");
            sink.Emit(BurstSmoothingSinkTests.Line("stuck"));
            await inner.Blocked.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            sink.Emit(BurstSmoothingSinkTests.Line("queued 1"));
            sink.Emit(BurstSmoothingSinkTests.Line("queued 2"));

            var disposal = sink.DisposeAsync().AsTask();
            await clock.WaitForTimerAsync(); // The flush timeout.
            clock.Advance(TimeSpan.FromSeconds(2));
            await clock.WaitForTimerAsync(); // The stop grace.
            clock.Advance(BurstSmoothingSink.StopGrace);
            await disposal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            selfLog
                .ToString()
                .Should()
                .Contain(
                    "EprRegisterEnrolBackend.Utils.Logging.BurstSmoothingSink: Dropped 3 log lines at shutdown: 3 were "
                        + "still queued after 00:00:02 and 0 were refused because the log queue was full or closed. "
                        + "The wrapped sink was still writing a line 00:00:01 after the flush timeout, so it was not "
                        + "disposed"
                );
            inner.Disposed.Should().BeFalse();
        }
        finally
        {
            SelfLog.Disable();
            inner.Release();
        }
    }
}
