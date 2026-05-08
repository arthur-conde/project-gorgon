using System.Diagnostics;

namespace Legolas.Rendering;

/// <summary>
/// Advances the active-segment dash offset to produce a "marching ants"
/// animation in immediate-mode rendering. Replaces the WPF version's
/// <c>StrokeDashOffset</c> Storyboard, which kept WPF's render loop ticking
/// every frame regardless of whether the active segment was visible
/// (continuous-invalidation cost was the dominant factor in the
/// pre-rewrite perf profile).
///
/// Speed defaults to 15 px/s — the WPF storyboard animated 0 → -9 over
/// 0.6 s, so dt-derived speed = -15. Direction: dash offset goes
/// negative so dashes appear to move "forward" along the segment in the
/// natural pen direction.
/// </summary>
internal sealed class MarchingAntsClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _lastSeconds;
    private double _offset;

    /// <summary>Pixels per second, signed. Negative = ants march forward.</summary>
    public double Speed { get; set; } = -15.0;

    /// <summary>
    /// Sum of the dash pattern (6 + 3 = 9 for the active segment). The
    /// offset wraps modulo this so the running total never grows past one
    /// period — float precision stays stable for arbitrarily long sessions.
    /// </summary>
    public double DashPeriod { get; set; } = 9.0;

    /// <summary>Advance the clock and return the current dash offset.</summary>
    public double Advance()
    {
        var nowSeconds = _watch.Elapsed.TotalSeconds;
        var dt = nowSeconds - _lastSeconds;
        _lastSeconds = nowSeconds;
        if (dt <= 0 || dt > 1.0) return _offset;  // skip first tick + outliers
        _offset += dt * Speed;
        if (DashPeriod > 0)
        {
            _offset %= DashPeriod;
        }
        return _offset;
    }
}
