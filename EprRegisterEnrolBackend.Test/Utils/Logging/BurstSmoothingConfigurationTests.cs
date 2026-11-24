using System.Text.Json;
using EprRegisterEnrolBackend.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

// These tests swap Console.Out and rely on the static Serilog logger, both process-wide, so nothing else runs alongside.
[CollectionDefinition(nameof(ProcessWideLogging), DisableParallelization = true)]
public sealed class ProcessWideLogging;

[Collection(nameof(ProcessWideLogging))]
public class BurstSmoothingConfigurationTests
{
    private const string EcsFormatter =
        "Elastic.CommonSchema.Serilog.EcsTextFormatter, Elastic.CommonSchema.Serilog";

    // appsettings.Development.json has no WriteTo of its own: .NET merges arrays by index, so one would merge into
    // the smoothing sink's entry. Development (and so the compose stack the end-to-end tests run against) smooths
    // bursts the same way every deployed environment does.
    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void Every_environment_writes_through_the_smoothing_sink_to_the_ecs_console(string environment)
    {
        var serilog = AppSettings(environment).GetSection("Serilog");

        serilog.GetSection("Using").Get<string[]>().Should().Equal("EprRegisterEnrolBackend");
        var writeTo = serilog.GetSection("WriteTo").GetChildren().ToList();
        writeTo.Should().ContainSingle();
        writeTo[0]["Name"].Should().Be(BurstSmoothingLoggerConfigurationExtensions.SinkName);
        var args = writeTo[0].GetSection("Args");
        args.GetChildren()
            .Select(a => a.Key)
            .Should()
            .BeEquivalentTo("linesPerSecond", "burst", "queueCapacity", "shutdownFlushTimeout", "configure");
        args["linesPerSecond"].Should().Be("200");
        args["burst"].Should().Be("50");
        args["queueCapacity"].Should().Be("10000");
        args["shutdownFlushTimeout"].Should().Be("00:00:05");
        var configure = args.GetSection("configure").GetChildren().ToList();
        configure.Should().ContainSingle();
        configure[0]["Name"].Should().Be("Console");
        configure[0].GetSection("Args").GetChildren().Select(a => a.Key).Should().Equal("formatter");
        configure[0]["Args:formatter"].Should().Be(EcsFormatter);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public void The_shipped_config_writes_ecs_json_lines_to_stdout(string environment)
    {
        var output = CaptureConsole(() =>
        {
            using var logger = new LoggerConfiguration()
                .ReadFrom.Configuration(AppSettings(environment))
                .CreateLogger();
            logger.ForContext("SourceContext", "BurstSmoothingConfigurationTests").Warning("smoothed {Number}", 42);
        });

        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(l => l.Contains("smoothed 42"));
        using var json = JsonDocument.Parse(line);
        json.RootElement.GetProperty("message").GetString().Should().Be("smoothed 42");
        json.RootElement.GetProperty("log.level").GetString().Should().Be("Warning");
        json.RootElement.GetProperty("log").GetProperty("logger").GetString()
            .Should()
            .Be("BurstSmoothingConfigurationTests");
    }

    // An environment variable such as Serilog__WriteTo__0__Args__linesPerSecond overrides a setting, and a bad
    // value stops the service starting with the setting named.
    [Theory]
    [InlineData("linesPerSecond", "0")]
    [InlineData("burst", "20000")]
    [InlineData("queueCapacity", "0")]
    [InlineData("shutdownFlushTimeout", "00:00:00")]
    public void An_overriding_setting_that_is_invalid_stops_startup_naming_it(string setting, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddConfiguration(AppSettings("Production"))
            .AddInMemoryCollection(
                new Dictionary<string, string?> { [$"Serilog:WriteTo:0:Args:{setting}"] = value }
            )
            .Build();

        var build = () => new LoggerConfiguration().ReadFrom.Configuration(configuration).CreateLogger();

        build
            .Should()
            .Throw<Exception>()
            .Which.ToString()
            .Should()
            .Contain($"The log burst smoothing setting {setting} (Serilog:WriteTo:SmoothBursts Args)");
    }

    // Program.cs relies on UseSerilog disposing the logger, and so flushing the smoothing sink, when the host stops.
    [Fact]
    public async Task Stopping_the_host_disposes_the_logger_and_its_sinks()
    {
        var probe = new DisposalProbe();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Host.UseSerilog((_, config) => config.WriteTo.Sink(probe));
        var app = builder.Build();

        // As Program.cs runs it.
        var run = app.RunAsync();
        await app.Lifetime.ApplicationStarted.WaitAsync(TestContext.Current.CancellationToken);
        app.Lifetime.StopApplication();
        await run;

        probe.Disposed.Should().BeTrue();
    }

    private static IConfiguration AppSettings(string environment) =>
        new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .Build();

    private static string CaptureConsole(Action log)
    {
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            log();
        }
        finally
        {
            Console.SetOut(original);
        }
        return captured.ToString();
    }

    private sealed class DisposalProbe : ILogEventSink, IDisposable
    {
        public bool Disposed { get; private set; }

        public void Emit(LogEvent logEvent) { }

        public void Dispose() => Disposed = true;
    }
}

file static class CancellationTokenExtensions
{
    public static Task WaitAsync(this CancellationToken token, CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource();
        token.Register(() => started.TrySetResult());
        return started.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }
}
