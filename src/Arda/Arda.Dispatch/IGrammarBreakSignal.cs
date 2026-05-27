using Arda.Contracts.State.Health;
using Microsoft.Extensions.Logging;

namespace Arda.Dispatch;

/// <summary>
/// Reports grammar drift observed by the Arda pipeline.
/// <para>
/// Two channels:
/// <list type="bullet">
///   <item><see cref="IsRaised"/> — halt requested. Companion driver stops at
///   its next loop iteration; the shell shows a blocking halt banner. Set only
///   in the default (non-tolerant) mode via <see cref="Raise"/>.</item>
///   <item><see cref="HasObservedBreak"/> — any break has been seen this
///   session. Gates composer snapshot writes; set by both <see cref="Raise"/>
///   and <see cref="MarkObserved"/> (the tolerant-mode path). Without this gate,
///   tolerant mode would silently persist divergent state to per-character
///   stores on next launch.</item>
/// </list>
/// <see cref="Raise"/> is a superset of <see cref="MarkObserved"/>: raising
/// also marks the break as observed, so halt-mode persistence stays gated.
/// </para>
/// </summary>
public interface IGrammarBreakSignal
{
    /// <summary>The first break captured this session, or null if none.</summary>
    GrammarBreak? Current { get; }

    /// <summary>True once <see cref="Raise"/> has been called. Drives halt.</summary>
    bool IsRaised { get; }

    /// <summary>
    /// True once either <see cref="Raise"/> or <see cref="MarkObserved"/> has
    /// been called. Drives composer persistence gating and the shell's
    /// tolerant-mode banner.
    /// </summary>
    bool HasObservedBreak { get; }

    /// <summary>Total breaks seen this session (halt + tolerant combined).</summary>
    int ObservedCount { get; }

    /// <summary>Default mode: halt + observe. Idempotent on Current.</summary>
    void Raise(GrammarBreak breakDetails);

    /// <summary>Tolerant mode: observe only. Loop continues; halt is not requested.</summary>
    void MarkObserved(GrammarBreak breakDetails);

    /// <summary>Fires on the first <see cref="Raise"/> (halt transition).</summary>
    event EventHandler? Raised;

    /// <summary>
    /// Fires on every <see cref="Raise"/> or <see cref="MarkObserved"/> call.
    /// Use for UI surfaces that want to update on every tolerant-mode break
    /// (e.g. "{ObservedCount} parse failures so far" in the banner).
    /// </summary>
    event EventHandler? ObservedBreakChanged;
}

internal sealed class GrammarBreakSignal : IGrammarBreakSignal
{
    private readonly object _gate = new();
    private readonly ILogger<GrammarBreakSignal>? _logger;
    private GrammarBreak? _current;
    private bool _raised;
    private int _observedCount;

    public GrammarBreakSignal(ILogger<GrammarBreakSignal>? logger = null) => _logger = logger;

    public GrammarBreak? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsRaised
    {
        get { lock (_gate) return _raised; }
    }

    public bool HasObservedBreak
    {
        get { lock (_gate) return _observedCount > 0; }
    }

    public int ObservedCount
    {
        get { lock (_gate) return _observedCount; }
    }

    public event EventHandler? Raised;
    public event EventHandler? ObservedBreakChanged;

    public void Raise(GrammarBreak breakDetails)
    {
        bool firstRaise;
        lock (_gate)
        {
            firstRaise = !_raised;
            _raised = true;
            _current ??= breakDetails;
            _observedCount++;
        }

        if (!firstRaise)
        {
            _logger?.LogDebug(
                "Subsequent grammar break for {Verb} ignored for halt purposes; first break ({FirstVerb}) stands",
                breakDetails.Verb, _current!.Verb);
        }

        ObservedBreakChanged?.Invoke(this, EventArgs.Empty);
        if (firstRaise)
            Raised?.Invoke(this, EventArgs.Empty);
    }

    public void MarkObserved(GrammarBreak breakDetails)
    {
        lock (_gate)
        {
            _current ??= breakDetails;
            _observedCount++;
        }
        ObservedBreakChanged?.Invoke(this, EventArgs.Empty);
    }
}
