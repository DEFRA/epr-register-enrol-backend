using System.Text.Json;
using Elastic.CommonSchema.Serilog;
using FluentAssertions;
using Serilog.Events;
using Serilog.Parsing;

namespace EprRegisterEnrolBackend.Test.AccreditationApi;

// RA-651: CDP keeps only a fixed set of log fields in OpenSearch, and event.duration (nanoseconds)
// is the one numeric field a latency panel or alert can use. The ECS formatter writes it only for a
// property named ElapsedMilliseconds, so that name is pinned here: renaming it would empty every
// latency panel without any error.
public class AccessLogEcsMappingTests
{
    private static JsonElement Format(string property, object value)
    {
        var template = new MessageTemplateParser().Parse("Service client request");
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            template,
            [new LogEventProperty(property, new ScalarValue(value))]
        );
        using var writer = new StringWriter();
        new EcsTextFormatter().Format(logEvent, writer);
        return JsonDocument.Parse(writer.ToString()).RootElement.Clone();
    }

    [Fact]
    public void ElapsedMilliseconds_IsWrittenAsEventDuration_InNanoseconds()
    {
        var json = Format("ElapsedMilliseconds", 12L);

        json.GetProperty("event").GetProperty("duration").GetInt64().Should().Be(12_000_000);
    }

    [Fact]
    public void AnyOtherName_IsNotEventDuration_SoCdpWouldDropIt()
    {
        var json = Format("DurationMs", 12L);

        json.GetProperty("event").TryGetProperty("duration", out _).Should().BeFalse();
    }
}
