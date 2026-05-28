using System.Diagnostics;
using Mithril.Shared.Diagnostics.Telemetry;

namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// <see cref="TraceListener"/> attached to
/// <c>PresentationTraceSources.DataBindingSource</c> while a perf-trace
/// session is active. Emits each distinct error message via
/// <see cref="MithrilActivitySources.Wpf"/> / <see cref="MithrilMeters.Wpf.BindingErrors"/>
/// with a per-message throttle so a broken binding that fails 60×/sec
/// doesn't drown the trace file. Throttle window is 1s per distinct
/// message; first occurrence in each window emits, the rest count silently.
/// </summary>
internal sealed class BindingErrorTraceListener : TraceListener
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTime> _lastEmit = new(StringComparer.Ordinal);
    private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(1);

    public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Emit(message); }
    public override void WriteLine(string? message) { if (!string.IsNullOrEmpty(message)) Emit(message); }

    private void Emit(string message)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_lastEmit.TryGetValue(message, out var last) && now - last < ThrottleWindow) return;
            _lastEmit[message] = now;
        }
        MithrilMeters.Wpf.BindingErrors.Add(1);
        using var act = MithrilActivitySources.Wpf.StartActivity("binding_error");
        act?.SetTag("message", message);
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
