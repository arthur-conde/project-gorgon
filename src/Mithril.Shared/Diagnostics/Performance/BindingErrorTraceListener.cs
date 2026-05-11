using System.Diagnostics;

namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// <see cref="TraceListener"/> attached to
/// <c>PresentationTraceSources.DataBindingSource</c> while a perf-trace
/// session is active. Forwards binding errors to <see cref="IPerfTracer"/>
/// with a per-message throttle so a broken binding that fails 60×/sec
/// doesn't drown the trace file. Throttle window is 1s per distinct
/// message; first occurrence in each window emits, the rest count silently.
/// </summary>
internal sealed class BindingErrorTraceListener : TraceListener
{
    private readonly IPerfTracer _tracer;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _lastEmit = new(StringComparer.Ordinal);
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(1);

    public BindingErrorTraceListener(IPerfTracer tracer) { _tracer = tracer; }

    public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Emit(message); }
    public override void WriteLine(string? message) { if (!string.IsNullOrEmpty(message)) Emit(message); }

    private void Emit(string message)
    {
        if (!_tracer.IsActive) return;
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_lastEmit.TryGetValue(message, out var last) && now - last < ThrottleWindow) return;
            _lastEmit[message] = now;
        }
        _tracer.EmitBindingError(message);
    }

    /// <summary>Test seam: returns true if a fresh emit of <paramref name="message"/> would be allowed right now.</summary>
    internal bool WouldEmit(string message)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            return !_lastEmit.TryGetValue(message, out var last) || now - last >= ThrottleWindow;
        }
    }
}
