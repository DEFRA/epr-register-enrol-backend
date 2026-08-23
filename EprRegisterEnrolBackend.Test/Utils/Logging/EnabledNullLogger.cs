using EprRegisterEnrolBackend.Test.Utils.Logging;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

/// <summary>
/// A logger that discards everything written to it but reports every level as
/// enabled.
///
/// <see cref="Microsoft.Extensions.Logging.Abstractions.NullLogger{T}"/> answers
/// <c>false</c> to <see cref="ILogger.IsEnabled"/>, so any production code guarded by
/// <c>if (logger.IsEnabled(...))</c> never runs under test. Substituting this logger
/// keeps the output discarded while still exercising those guarded branches.
/// </summary>
internal sealed class EnabledNullLogger<T> : ILogger<T>
{
    public static readonly EnabledNullLogger<T> Instance = new();

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    ) { }
}
