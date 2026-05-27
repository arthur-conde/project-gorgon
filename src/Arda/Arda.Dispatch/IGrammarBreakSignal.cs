using Arda.Contracts.State.Health;
using Microsoft.Extensions.Logging;

namespace Arda.Dispatch;

/// <summary>
/// One-shot signal raised when the first <see cref="GrammarException"/> escapes
/// a <c>WorldDriver</c>. Subscribers (the companion driver, the shell health
/// view) react to <see cref="Raised"/> to halt cooperatively.
/// </summary>
public interface IGrammarBreakSignal
{
    GrammarBreak? Current { get; }
    bool IsRaised { get; }
    void Raise(GrammarBreak breakDetails);
    event EventHandler? Raised;
}

internal sealed class GrammarBreakSignal : IGrammarBreakSignal
{
    private readonly object _gate = new();
    private readonly ILogger<GrammarBreakSignal>? _logger;
    private GrammarBreak? _current;

    public GrammarBreakSignal(ILogger<GrammarBreakSignal>? logger = null) => _logger = logger;

    public GrammarBreak? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsRaised
    {
        get { lock (_gate) return _current is not null; }
    }

    public event EventHandler? Raised;

    public void Raise(GrammarBreak breakDetails)
    {
        bool first;
        lock (_gate)
        {
            if (_current is null)
            {
                _current = breakDetails;
                first = true;
            }
            else
            {
                first = false;
            }
        }

        if (first)
        {
            Raised?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _logger?.LogDebug(
                "Subsequent grammar break for {Verb} ignored; first break ({FirstVerb}) stands",
                breakDetails.Verb, _current!.Verb);
        }
    }
}
