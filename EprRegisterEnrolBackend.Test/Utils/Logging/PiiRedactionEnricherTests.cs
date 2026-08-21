using Elastic.CommonSchema;
using Elastic.CommonSchema.Serilog;
using EprRegisterEnrolBackend.Utils.Logging;
using FluentAssertions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

public class PiiRedactionEnricherTests
{
    private static readonly MessageTemplateParser TemplateParser = new();

    [Fact]
    public void Enrich_RedactsHttpContextClientIpAndAddress_WhenPresent()
    {
        var enrichments = new SpecialProperties.HttpContextEnrichments
        {
            Client = new Client { Ip = "203.0.113.5", Address = "203.0.113.5" },
        };
        var logEvent = CreateLogEvent(
            new LogEventProperty(
                SpecialProperties.SpecialKeys.HttpContext,
                new ScalarValue(enrichments)
            )
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        enrichments.Client!.Ip.Should().Be(PiiRedactionEnricher.RedactedValue);
        enrichments.Client!.Address.Should().Be(PiiRedactionEnricher.RedactedValue);
    }

    [Fact]
    public void Enrich_DoesNotThrow_WhenHttpContextPropertyIsAbsent()
    {
        var logEvent = CreateLogEvent();

        var act = () => new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        act.Should().NotThrow();
    }

    [Fact]
    public void Enrich_DoesNotThrow_WhenHttpContextHasNoClient()
    {
        var enrichments = new SpecialProperties.HttpContextEnrichments { Client = null };
        var logEvent = CreateLogEvent(
            new LogEventProperty(
                SpecialProperties.SpecialKeys.HttpContext,
                new ScalarValue(enrichments)
            )
        );

        var act = () => new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        act.Should().NotThrow();
    }

    [Fact]
    public void Enrich_RedactsTopLevelEmailProperty_WhenPresent()
    {
        var logEvent = CreateLogEvent(
            new LogEventProperty("Email", new ScalarValue("person@example.com"))
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        logEvent
            .Properties["Email"]
            .Should()
            .BeEquivalentTo(new ScalarValue(PiiRedactionEnricher.RedactedValue));
    }

    [Fact]
    public void Enrich_LeavesNonPiiPropertiesUntouched()
    {
        var logEvent = CreateLogEvent(
            new LogEventProperty("OrganisationName", new ScalarValue("Acme Ltd")),
            new LogEventProperty("CorrelationId", new ScalarValue("abc-123")),
            new LogEventProperty("ApplicationId", new ScalarValue("app-456"))
        );

        new PiiRedactionEnricher().Enrich(logEvent, new TestPropertyFactory());

        logEvent
            .Properties["OrganisationName"]
            .Should()
            .BeEquivalentTo(new ScalarValue("Acme Ltd"));
        logEvent.Properties["CorrelationId"].Should().BeEquivalentTo(new ScalarValue("abc-123"));
        logEvent.Properties["ApplicationId"].Should().BeEquivalentTo(new ScalarValue("app-456"));
    }

    private static LogEvent CreateLogEvent(params LogEventProperty[] properties) =>
        new(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            null,
            TemplateParser.Parse("Test message"),
            properties
        );

    private sealed class TestPropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(
            string name,
            object? value,
            bool destructureObjects = false
        ) => new(name, new ScalarValue(value));
    }
}
