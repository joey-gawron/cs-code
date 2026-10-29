using Microsoft.Extensions.Logging;

namespace CsCode.Platform.Tests.Support;

/// <summary>Records every log call so tests can assert "logged once" / "logged an error".</summary>
public sealed class CapturingLogger<T> : ILogger<T>, ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries { get { lock (_entries) return _entries.ToArray(); } }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
    }
}
