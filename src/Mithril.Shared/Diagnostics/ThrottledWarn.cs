using Microsoft.Extensions.Logging;

namespace Mithril.Shared.Diagnostics;

/// <summary>
/// Per-instance throttled <c>Warn</c> for hot-path error containment. A log
/// ingestion loop must never die on one bad line, but emitting a <c>Warn</c>
/// per bad line would reproduce the unbuffered per-line sink cost that
/// mithril#507 is about (a pathological run of malformed lines → a Warn
/// flood through the diagnostics file logger). This emits at most
/// one Warn per <paramref name="window"/> and rolls the suppressed count into
/// the next emitted message, so the failure stays observable without the
/// flood.
/// </summary>
/// <remarks>
/// Thread-safe (a single ingestion loop is the expected single writer; the
/// lock keeps it correct if shared). A null sink makes <see cref="Warn"/> a
/// no-op, mirroring optional-<c>ILogger</c> convention used by ingestion services.
/// </remarks>
public sealed class ThrottledWarn
{
    private readonly ILogger? _logger;
    private readonly string _category;
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private long _suppressed;
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;

    public ThrottledWarn(
        ILogger? logger,
        string category,
        TimeSpan? window = null,
        TimeProvider? time = null)
    {
        _logger = logger;
        _category = category;
        _time = time ?? TimeProvider.System;
        _window = window ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Emit <paramref name="message"/> as a Warn if the throttle window has
    /// elapsed; otherwise count it as suppressed and emit nothing. The first
    /// message after a suppressed run carries a "(+N suppressed)" rollup.
    /// </summary>
    public void Warn(string message)
    {
        if (_logger is null) return;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now < _nextAllowed)
            {
                _suppressed++;
                return;
            }

            var suppressed = _suppressed;
            _suppressed = 0;
            _nextAllowed = now + _window;
            _logger.LogDiagnosticWarn(
                _category,
                suppressed > 0
                    ? $"{message} (+{suppressed} similar suppressed in last {_window.TotalSeconds:0}s)"
                    : message);
        }
    }
}
