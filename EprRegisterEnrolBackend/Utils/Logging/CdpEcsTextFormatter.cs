using Elastic.CommonSchema;
using Elastic.CommonSchema.Serilog;
using Serilog.Events;

namespace EprRegisterEnrolBackend.Utils.Logging;

/// <summary>
/// The ECS formatter, taking from the line what ECS would otherwise read from the thread that formats it.
/// <see cref="BurstSmoothingSink"/> formats every line on its reader, not on the thread that logged it. ECS 9 fills
/// <c>trace.id</c> and <c>span.id</c> from <c>Activity.Current</c>, and <c>process.thread.id</c> from the current
/// thread, as it formats, so on the reader it would write no trace and the reader's thread. This writes the trace and
/// span Serilog records on the line when it is logged, and the thread the sink records then (<see cref="ThreadId"/>,
/// <see cref="ThreadName"/>).
/// </summary>
public sealed class CdpEcsTextFormatter()
    : EcsTextFormatter(new EcsTextFormatterConfiguration { MapCustom = FromTheLoggingThread })
{
    /// <summary>The property holding the id of the thread that logged the line. ECS reads it, so it adds no field.</summary>
    public const string ThreadId = nameof(ThreadId);

    /// <summary>The property holding the name of the thread that logged the line, when it has one.</summary>
    public const string ThreadName = nameof(ThreadName);

    internal static EcsDocument FromTheLoggingThread(EcsDocument document, LogEvent logEvent)
    {
        // The line's own record decides, including that it has none: a line logged outside a trace gets no trace.id
        // from whatever the formatting thread is doing.
        document.TraceId = logEvent.TraceId?.ToHexString();
        document.SpanId = logEvent.SpanId?.ToHexString();
        if (document.Process is { } process && Scalar(logEvent, ThreadId) is int threadId)
        {
            process.ThreadId = threadId;
            process.ThreadName = Scalar(logEvent, ThreadName) as string;
        }

        return document;
    }

    private static object? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue scalar ? scalar.Value : null;
}
