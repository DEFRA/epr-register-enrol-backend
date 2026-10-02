using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

/// <summary>
/// An <see cref="ILoggerFactory"/> that records the rendered message of every log entry from
/// every logger it creates. For endpoint tests that need to assert on what a handler logged
/// through an injected <see cref="ILoggerFactory"/>: the app's Serilog host replaces the
/// standard logging providers, so registering an extra provider would never see the entries.
/// </summary>
public sealed class CapturingLoggerFactory : ILoggerFactory
{
    public sealed record Entry(string Category, LogLevel LogLevel, string Message);

    private readonly ConcurrentQueue<Entry> _entries = new();

    public IReadOnlyList<Entry> Entries => [.. _entries];

    public void Clear() => _entries.Clear();

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    public void AddProvider(ILoggerProvider provider) { }

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => entries.Enqueue(new Entry(category, logLevel, formatter(state, exception)));
    }
}
