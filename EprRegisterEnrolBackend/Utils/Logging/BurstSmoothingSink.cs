using System.Threading.Channels;
using Serilog.Core;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Parsing;

namespace EprRegisterEnrolBackend.Utils.Logging;

/// <summary>
/// Passes every log line on to the wrapped sink at a steady rate, so the service never writes hundreds of lines in a
/// burst. CDP's log shipping looks to drop bursts: on ext-test ~400 lines in under 100 ms never reached OpenSearch,
/// and nor did anything else logged in that second (RA-784). See the README's Logging section.
/// <para>
/// The wrapped sink formats each line on the reader, not on the thread that logged it, so <see cref="Emit"/> records the
/// logging thread on the line (<see cref="CdpEcsTextFormatter.ThreadId"/>, <see cref="CdpEcsTextFormatter.ThreadName"/>)
/// for <see cref="CdpEcsTextFormatter"/> to write. Serilog has already recorded the trace and span on it.
/// </para>
/// <para>
/// A token bucket: up to <see cref="BurstSmoothingSettings.Burst"/> lines go out at once, then at most
/// <see cref="BurstSmoothingSettings.LinesPerSecond"/> a second. <see cref="Emit"/> only queues the line, so logging
/// never waits; one reader writes the queue in order. A line that finds the queue full is dropped and counted, and the
/// count goes out as one Error line before the next line written. On disposal the queue is flushed for up to
/// <see cref="BurstSmoothingSettings.ShutdownFlushTimeout"/>, and whatever could not be written is reported in one
/// final Error line. Error, so that lost log lines are noticed: the "5xx and Errors" alert reports them.
/// </para>
/// <para>
/// Disposal waits for the reader for at most <see cref="BurstSmoothingSettings.ShutdownFlushTimeout"/> plus
/// <see cref="StopGrace"/> (1 second). If the wrapped sink is still stuck writing a line by then, the final report goes
/// to SelfLog (stderr) instead, and the wrapped sink is not disposed. Otherwise disposal then writes the report and
/// disposes the wrapped sink, which isn't bounded: neither is a plain console sink's.
/// </para>
/// </summary>
public sealed class BurstSmoothingSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    /// <summary>The <c>log.logger</c> of the sink's own Error lines, to filter them in OpenSearch.</summary>
    public static readonly string SourceContext = typeof(BurstSmoothingSink).FullName!;

    /// <summary>
    /// How long disposal waits for the reader to stop once the flush timeout has run out and it has been told to. The
    /// reader checks for the stop before every line, so a stopped reader only has to finish the line it is writing,
    /// and a write still going after this is stuck. Disposal therefore waits for the reader for at most
    /// <see cref="BurstSmoothingSettings.ShutdownFlushTimeout"/> plus this.
    /// </summary>
    internal static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(1);

    private static readonly MessageTemplateParser s_parser = new();

    private static readonly MessageTemplate s_droppedTemplate = s_parser.Parse(
        "Dropped {DroppedLogLines} log lines because the log queue was full or closed"
    );

    private static readonly MessageTemplate s_shutdownTemplate = s_parser.Parse(
        "Dropped {DroppedLogLines} log lines at shutdown: {UnflushedLogLines} were still queued after "
            + "{ShutdownFlushTimeout} and {RefusedLogLines} were refused because the log queue was full or closed"
    );

    private readonly ILogEventSink _inner;
    private readonly BurstSmoothingSettings _settings;
    private readonly TimeProvider _clock;
    private readonly Channel<LogEvent> _queue;
    private readonly CancellationTokenSource _stopWriting = new();
    private readonly Task _reader;

    // Written by the reader only. The bucket is kept in ticks of tokens: one line costs TicksPerSecond, and every
    // elapsed tick adds LinesPerSecond, so the arithmetic is exact.
    private long _bucket;
    private long _lastRefill;

    // 1 while the reader holds a line it has taken off the queue but not yet written, so a stop counts it.
    private int _holdingLine;

    private long _dropped;
    private int _disposed;
    private int _finalReportTaken;

    public BurstSmoothingSink(ILogEventSink inner, BurstSmoothingSettings settings, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(clock);
        settings.Validate();

        _inner = inner;
        _settings = settings;
        _clock = clock;
        _queue = Channel.CreateBounded<LogEvent>(
            new BoundedChannelOptions(settings.QueueCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait, // Never waits: Emit uses TryWrite, which fails when full.
            }
        );
        _bucket = settings.Burst * TimeSpan.TicksPerSecond;
        _lastRefill = clock.GetTimestamp();
        // No token: ReadAsync watches _stopWriting itself, and a cancelled token here would only stop the reader ever
        // starting, losing every line.
        _reader = Task.Run(ReadAsync, CancellationToken.None);
    }

    /// <summary>The reader, for tests: it ends once the queue is written out or the reader is stopped.</summary>
    internal Task Reader => _reader;

    public void Emit(LogEvent logEvent)
    {
        var thread = Thread.CurrentThread;
        logEvent.AddPropertyIfAbsent(Property(CdpEcsTextFormatter.ThreadId, thread.ManagedThreadId));
        if (thread.Name is { } threadName)
            logEvent.AddPropertyIfAbsent(Property(CdpEcsTextFormatter.ThreadName, threadName));
        if (_queue.Writer.TryWrite(logEvent))
            return;

        Interlocked.Increment(ref _dropped);
        if (Volatile.Read(ref _finalReportTaken) != 0)
        {
            // The final report has been taken, so nothing would ever count this one.
            SelfLog.WriteLine("{0}: a log line was written after disposal and is lost", SourceContext);
        }
    }

    private async Task ReadAsync()
    {
        var stopping = _stopWriting.Token;
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stopping))
            {
                while (_queue.Reader.TryRead(out var logEvent))
                {
                    _holdingLine = 1;
                    await TakeTokenAsync(stopping);
                    // Taken only once the line can go out, so a stop never loses the count.
                    var dropped = Interlocked.Exchange(ref _dropped, 0);
                    if (dropped > 0)
                    {
                        Write(ErrorLine(s_droppedTemplate, Property("DroppedLogLines", dropped)));
                        await TakeTokenAsync(stopping);
                    }

                    Write(logEvent);
                    _holdingLine = 0;
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // Stopped at the flush timeout; DisposeAsync counts what is left.
        }
    }

    private async Task TakeTokenAsync(CancellationToken stopping)
    {
        while (true)
        {
            // Checked even when a token is free, so a stopped reader writes no more than the line it is on: with a slow
            // console every write outlasts a token, and the cancellable delay below would never be reached.
            stopping.ThrowIfCancellationRequested();
            var now = _clock.GetTimestamp();
            var elapsedTicks = _clock.GetElapsedTime(_lastRefill, now).Ticks;
            _lastRefill = now;
            var full = _settings.Burst * TimeSpan.TicksPerSecond;
            // Capped at the time it takes to fill the bucket before multiplying, so a long idle spell can't overflow.
            var fillTicks = full / _settings.LinesPerSecond + 1;
            _bucket = Math.Min(full, _bucket + Math.Min(elapsedTicks, fillTicks) * _settings.LinesPerSecond);
            if (_bucket >= TimeSpan.TicksPerSecond)
            {
                _bucket -= TimeSpan.TicksPerSecond;
                return;
            }

            var waitTicks =
                (TimeSpan.TicksPerSecond - _bucket + _settings.LinesPerSecond - 1) / _settings.LinesPerSecond;
            await Task.Delay(TimeSpan.FromTicks(waitTicks), _clock, stopping);
        }
    }

    // A failing inner sink must not stop the reader, or every later line would be lost. Serilog reports sink
    // failures through SelfLog, which CdpLogging sends to stderr.
    private void Write(LogEvent logEvent)
    {
        try
        {
            _inner.Emit(logEvent);
        }
        catch (Exception ex)
        {
            SelfLog.WriteLine("{0}: the wrapped sink failed to write a log line: {1}", SourceContext, ex);
        }
    }

    private LogEvent ErrorLine(MessageTemplate template, params LogEventProperty[] properties) =>
        new(
            _clock.GetUtcNow(),
            LogEventLevel.Error,
            null,
            template,
            [.. properties, Property("SourceContext", SourceContext)]
        );

    private static LogEventProperty Property(string name, object value) =>
        new(name, new ScalarValue(value));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _queue.Writer.TryComplete();
        var stuck = false;
        try
        {
            // Bounded only by the flush timeout: _stopWriting is what stops the reader once that runs out.
            await _reader.WaitAsync(_settings.ShutdownFlushTimeout, _clock, CancellationToken.None);
        }
        catch (TimeoutException)
        {
            await _stopWriting.CancelAsync();
            stuck = !await StoppedWithinAsync(StopGrace);
        }

        // Counted, not read: a stuck reader may still take lines off the queue, and it is the only reader.
        var unflushed = Volatile.Read(ref _holdingLine) + _queue.Reader.Count;
        Volatile.Write(ref _finalReportTaken, 1);
        var refused = Interlocked.Exchange(ref _dropped, 0);
        var report =
            unflushed + refused > 0
                ? ErrorLine(
                    s_shutdownTemplate,
                    Property("DroppedLogLines", unflushed + refused),
                    Property("UnflushedLogLines", unflushed),
                    Property("ShutdownFlushTimeout", _settings.ShutdownFlushTimeout),
                    Property("RefusedLogLines", refused)
                )
                : null;

        if (stuck)
        {
            // The wrapped sink is blocked mid-write (say, stdout no longer draining), so it can neither take the report
            // nor be disposed. The report goes to stderr instead, and shutdown carries on.
            SelfLog.WriteLine(
                "{0}: {1}. The wrapped sink was still writing a line {2} after the flush timeout, so it was not disposed",
                SourceContext,
                report?.RenderMessage() ?? "Dropped no log lines at shutdown",
                StopGrace
            );
            return;
        }

        if (report is not null)
            Write(report);

        _stopWriting.Dispose();
        switch (_inner)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private async Task<bool> StoppedWithinAsync(TimeSpan grace)
    {
        try
        {
            // Not cancellable: the grace is the bound.
            await _reader.WaitAsync(grace, _clock, CancellationToken.None);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // Serilog disposes sinks synchronously (Logger.Dispose, Log.CloseAndFlush). ASP.NET Core has no synchronisation
    // context, so blocking here can't deadlock.
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
