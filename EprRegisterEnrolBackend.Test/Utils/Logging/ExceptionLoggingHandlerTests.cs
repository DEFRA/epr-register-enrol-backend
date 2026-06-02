using EprRegisterEnrolBackend.Utils.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

public class ExceptionLoggingHandlerTests
{
    [Fact]
    public async Task TryHandleAsync_LogsExceptionAtErrorLevel()
    {
        var logger = new CapturingLogger<ExceptionLoggingHandler>();
        var problemDetailsService = Substitute.For<IProblemDetailsService>();
        var handler = new ExceptionLoggingHandler(logger, problemDetailsService);

        var exception = new Exception("test error");
        await handler.TryHandleAsync(new DefaultHttpContext(), exception, CancellationToken.None);

        logger
            .Entries.Should()
            .ContainSingle(e => e.LogLevel == LogLevel.Error && e.Exception == exception);
    }

    [Fact]
    public async Task TryHandleAsync_Sets500StatusCode()
    {
        var logger = new CapturingLogger<ExceptionLoggingHandler>();
        var problemDetailsService = Substitute.For<IProblemDetailsService>();
        var handler = new ExceptionLoggingHandler(logger, problemDetailsService);

        var context = new DefaultHttpContext();
        await handler.TryHandleAsync(context, new Exception(), CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }

    [Fact]
    public async Task TryHandleAsync_ReturnsResultFromProblemDetailsService()
    {
        var logger = new CapturingLogger<ExceptionLoggingHandler>();
        var problemDetailsService = Substitute.For<IProblemDetailsService>();
        problemDetailsService.TryWriteAsync(Arg.Any<ProblemDetailsContext>()).Returns(true);
        var handler = new ExceptionLoggingHandler(logger, problemDetailsService);

        var result = await handler.TryHandleAsync(
            new DefaultHttpContext(),
            new Exception(),
            CancellationToken.None
        );

        result.Should().BeTrue();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel LogLevel, string Message, Exception? Exception)> Entries { get; } =
        [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
