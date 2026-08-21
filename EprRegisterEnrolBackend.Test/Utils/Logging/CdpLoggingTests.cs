using EprRegisterEnrolBackend.Utils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

/// <summary>
/// Pins that <see cref="PiiRedactionEnricher"/> is actually wired into the
/// real <see cref="CdpLogging.Configuration"/> pipeline (not just correct
/// in isolation, as <see cref="PiiRedactionEnricherTests"/> already covers).
/// </summary>
public class CdpLoggingTests
{
    [Fact]
    public void Configuration_RedactsEmail_InComposedPipeline()
    {
        var sink = new CapturingSink();
        var config = new LoggerConfiguration();

        CdpLogging.Configuration(NewHostBuilderContext(), config);
        using var logger = config.WriteTo.Sink(sink).CreateLogger();

        logger.Information("Notify send starting {Email}", "person@example.com");

        var evt = Assert.Single(sink.Events);
        evt.Properties["Email"].ToString().Should().Contain(PiiRedactionEnricher.RedactedValue);
        evt.Properties["Email"].ToString().Should().NotContain("person@example.com");
    }

    private static HostBuilderContext NewHostBuilderContext() =>
        new(new Dictionary<object, object>())
        {
            HostingEnvironment = new TestHostEnvironment(),
            Configuration = new ConfigurationBuilder().Build(),
        };

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
