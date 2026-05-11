using System.Diagnostics;

namespace Mithril.Shared.Diagnostics.Performance;

/// <summary>
/// Disposable timing scope returned by <see cref="IPerfTracer.Scope"/>. A
/// default-constructed scope (returned when no session is active) does
/// nothing on dispose, so callers pay essentially nothing when tracing is
/// off — no allocation, no virtual dispatch, just a struct copy and a null
/// check.
/// </summary>
public readonly struct PerfScope : IDisposable
{
    private readonly IPerfTracer? _tracer;
    private readonly string? _name;
    private readonly long _startTicks;
    private readonly object? _tags;

    internal PerfScope(IPerfTracer tracer, string name, long startTicks, object? tags)
    {
        _tracer = tracer;
        _name = name;
        _startTicks = startTicks;
        _tags = tags;
    }

    public void Dispose()
    {
        if (_tracer is null || _name is null) return;
        var elapsedMs = (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;
        _tracer.EmitScope(_name, elapsedMs, _tags);
    }
}
