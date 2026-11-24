using System.Diagnostics.CodeAnalysis;
using Elastic.Serilog.Enrichers.Web;
using Serilog;
using Serilog.Debugging;

namespace EprRegisterEnrolBackend.Utils.Logging;

public static class CdpLogging
{
    [ExcludeFromCodeCoverage]
    public static void Configuration(HostBuilderContext ctx, LoggerConfiguration config)
    {
        var httpAccessor = ctx.Configuration.Get<HttpContextAccessor>();
        var traceIdHeader = ctx.Configuration.GetValue<string>("TraceHeader");
        var serviceVersion = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "";

        // Serilog reports its own failures, such as a sink that throws (see BurstSmoothingSink), only through
        // SelfLog, which writes nowhere until it is given somewhere to write.
        SelfLog.Enable(Console.Error);

        config
            .ReadFrom.Configuration(ctx.Configuration)
            .Enrich.WithEcsHttpContext(httpAccessor!)
            .Enrich.FromLogContext()
            .Enrich.With<PiiRedactionEnricher>()
            .Enrich.WithProperty("service.version", serviceVersion);

        if (traceIdHeader != null)
        {
            config.Enrich.WithCorrelationId(traceIdHeader);
        }
    }
}
