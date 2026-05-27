using Arda.Abstractions.Logs;
using Arda.Contracts;
using Arda.Dispatch;
using Arda.World.Player.Events;

namespace Arda.World.Player.Internal;

/// <summary>
/// Tracks the current moon phase from <c>ProcessSetCelestialInfo(phase)</c>.
/// Classifies the raw token into a <see cref="MoonPhase"/> enum and derives
/// a human-readable display name. Deduplicates same-phase re-emissions and
/// resets on character switch.
/// </summary>
internal sealed class Celestial : IFrameHandler, ICelestialState
{
    private readonly IDomainEventPublisher _bus;

    public string? CurrentPhaseRaw { get; private set; }
    public MoonPhase Phase { get; private set; }
    public string? DisplayName { get; private set; }
    public DateTimeOffset? MeasuredAt { get; private set; }

    public Celestial(IDomainEventPublisher bus) => _bus = bus;

    internal void Reset()
    {
        CurrentPhaseRaw = null;
        Phase = MoonPhase.Unknown;
        DisplayName = null;
        MeasuredAt = null;
    }

    public void Handle(ReadOnlySpan<char> args, ReadOnlySpan<char> verb, string sourceLog, LogLineMetadata metadata)
    {
        var tok = new ArgTokenizer(args, verb, sourceLog);
        tok.SkipOpen();

        var phaseSpan = tok.NextTokenSpan();
        if (phaseSpan.IsEmpty)
            return;

        var phase = phaseSpan.ToString();

        if (string.Equals(CurrentPhaseRaw, phase, StringComparison.Ordinal))
            return;

        var previous = CurrentPhaseRaw;
        CurrentPhaseRaw = phase;
        Phase = MoonPhaseExtensions.ParsePhase(phase);
        DisplayName = Phase.DisplayName(phase);
        MeasuredAt = metadata.Timestamp ?? metadata.ReadOn;
        _bus.Publish(new CelestialInfoChanged(previous, phase, Phase, DisplayName, metadata));
    }
}
