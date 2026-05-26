using Arda.Abstractions.Logs;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the current moon phase from <c>ProcessSetCelestialInfo(phase)</c>.
/// Deduplicates same-phase re-emissions and resets on character switch.
/// </summary>
internal sealed class Celestial : IFrameHandler, ICelestialState
{
    private readonly IDomainEventPublisher _bus;

    public string? CurrentPhaseRaw { get; private set; }

    public Celestial(IDomainEventPublisher bus) => _bus = bus;

    internal void Reset() => CurrentPhaseRaw = null;

    public void Handle(ReadOnlySpan<char> args, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args);
        tok.SkipOpen();

        var phaseSpan = tok.NextTokenSpan();
        if (phaseSpan.IsEmpty)
            return;

        var phase = phaseSpan.ToString();

        if (string.Equals(CurrentPhaseRaw, phase, StringComparison.Ordinal))
            return;

        var previous = CurrentPhaseRaw;
        CurrentPhaseRaw = phase;
        _bus.Publish(new CelestialInfoChanged(previous, phase, metadata));
    }
}
