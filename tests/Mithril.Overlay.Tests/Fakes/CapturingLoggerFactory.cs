using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Mithril.Overlay.Tests.Fakes;

/// <summary>Minimal <see cref="ILoggerFactory"/> that captures every log
/// entry into a shared list so tests can assert on log content +
/// exception shape (review iteration-1 B1: scene-drawer exceptions must
/// surface as <c>LogError</c>).</summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

    public void AddProvider(ILoggerProvider provider) { /* no-op */ }
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<CapturedLogEntry> _sink;
        public CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _sink.Enqueue(new CapturedLogEntry(_category, logLevel, formatter(state, exception), exception));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

internal sealed record CapturedLogEntry(string Category, LogLevel Level, string Message, Exception? Exception);
