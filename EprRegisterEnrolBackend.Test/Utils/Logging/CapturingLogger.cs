using Microsoft.Extensions.Logging;

namespace EprRegisterEnrolBackend.Test.Utils.Logging;

/// <summary>
/// Records every log entry, including any structured properties attached via
/// <see cref="ILogger.BeginScope{TState}"/>. Used to prove that sensitive
/// content (e.g. an HTTP request/response body) is attached as a scoped
/// property rather than interpolated into the rendered message — the
/// distinction that determines whether CDP's OpenSearch field allow-list
/// actually filters it out, since the `message` field is always indexed
/// regardless of content.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public sealed record Entry(
        LogLevel LogLevel,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> ScopeProperties
    );

    public List<Entry> Entries { get; } = [];

    private readonly List<IReadOnlyDictionary<string, object?>> _scopeStack = [];

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
    {
        var properties = state as IEnumerable<KeyValuePair<string, object?>>;
        var snapshot = properties?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
        _scopeStack.Add(snapshot);
        return new ScopePop(_scopeStack);
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter
    )
    {
        var mergedScope = new Dictionary<string, object?>();
        foreach (var scope in _scopeStack)
        {
            foreach (var (key, value) in scope)
            {
                mergedScope[key] = value;
            }
        }

        Entries.Add(new Entry(logLevel, formatter(state, exception), exception, mergedScope));
    }

    private sealed class ScopePop(List<IReadOnlyDictionary<string, object?>> stack) : IDisposable
    {
        public void Dispose() => stack.RemoveAt(stack.Count - 1);
    }
}
