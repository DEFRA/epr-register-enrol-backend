using System.Diagnostics;
using System.Text.Json;
using EprRegisterEnrolBackend.Utils.Logging;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

public class CdpEcsTextFormatterTests
{
    [Fact]
    public void The_trace_span_and_thread_come_from_the_line_not_the_thread_formatting_it()
    {
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();

        var json = Format(
            Line(traceId, spanId, new LogEventProperty(CdpEcsTextFormatter.ThreadId, new ScalarValue(4242)))
        );

        json.GetProperty("trace.id").GetString().Should().Be(traceId.ToHexString());
        json.GetProperty("span.id").GetString().Should().Be(spanId.ToHexString());
        json.GetProperty("process").GetProperty("thread.id").GetInt32().Should().Be(4242);
        json.GetProperty("process").TryGetProperty("thread.name", out _).Should().BeFalse("that thread had no name");
    }

    // The sink's own lines are written by its reader, so they have no trace and no recorded thread.
    [Fact]
    public void A_line_with_no_trace_or_thread_recorded_gets_neither_from_the_formatting_thread()
    {
        using var activity = new Activity("formatting thread").Start();

        var json = Format(Line(null, null));

        json.TryGetProperty("trace.id", out _).Should().BeFalse();
        json.TryGetProperty("span.id", out _).Should().BeFalse();
        json.GetProperty("process").GetProperty("thread.id").GetInt32().Should().Be(Environment.CurrentManagedThreadId);
    }

    private static LogEvent Line(ActivityTraceId? traceId, ActivitySpanId? spanId, params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Warning,
            null,
            new MessageTemplateParser().Parse("a line"),
            properties,
            traceId ?? default,
            spanId ?? default
        );

    private static JsonElement Format(LogEvent logEvent)
    {
        using var writer = new StringWriter();
        new CdpEcsTextFormatter().Format(logEvent, writer);
        return JsonDocument.Parse(writer.ToString()).RootElement.Clone();
    }
}
