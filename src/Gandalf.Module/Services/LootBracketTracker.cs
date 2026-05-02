using System.Text.RegularExpressions;
using Gandalf.Parsing;
using Mithril.Shared.Logging;

namespace Gandalf.Services;

/// <summary>
/// Signal-driven state machine that distinguishes a loot-chest interaction
/// from a storage-vault or NPC-dialog interaction. Replaces the v1 substring
/// heuristic that filtered chest prefab names by <c>Contains("StaticChest")</c>
/// — that filter silently dropped <c>EltibuleSecretChest</c> and would
/// regress on every game patch that introduces a new chest theme.
///
/// Bracket shapes (verified against captured Player.log):
/// <list type="bullet">
/// <item><b>Loot chest:</b> <c>ProcessStartInteraction → ProcessAddItem(...) → ProcessEnableInteractors([],[id,])</c></item>
/// <item><b>Storage / NPC:</b> <c>ProcessStartInteraction → ProcessPreTalkScreen → ProcessTalkScreen</c></item>
/// <item><b>Cooldown rejection:</b> <c>ProcessStartInteraction → ProcessScreenText("You've already looted...")</c></item>
/// </list>
/// The tracker maintains a tiny three-state machine and routes confirmed loot
/// events into <see cref="LootSource"/>; storage / NPC interactions are
/// silently discarded.
/// </summary>
public sealed partial class LootBracketTracker
{
    private readonly LootSource _source;
    private readonly ChestInteractionParser _interactionParser;
    private readonly ChestRejectionParser _rejectionParser;
    private readonly InteractionEndParser _endParser;
    private readonly InteractionDelayLoopParser _delayLoopParser;

    private State _state = State.Idle;
    private string? _bracketName;
    private DateTime _bracketStartTimestamp;
    private long _bracketInteractorId;
    private string? _bracketHarvestVerb;

    public LootBracketTracker(
        LootSource source,
        ChestInteractionParser interactionParser,
        ChestRejectionParser rejectionParser,
        InteractionEndParser endParser,
        InteractionDelayLoopParser delayLoopParser)
    {
        _source = source;
        _interactionParser = interactionParser;
        _rejectionParser = rejectionParser;
        _endParser = endParser;
        _delayLoopParser = delayLoopParser;
    }

    /// <summary>True iff the tracker is currently inside an interaction bracket.</summary>
    public bool IsInFlight => _state != State.Idle;

    [GeneratedRegex(
        """LocalPlayer:\s*Process(?:Pre)?TalkScreen\(""",
        RegexOptions.CultureInvariant)]
    private static partial Regex TalkScreenRx();

    [GeneratedRegex(
        """LocalPlayer:\s*ProcessAddItem\(""",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddItemRx();

    [GeneratedRegex(
        """LocalPlayer:\s*ProcessEnableInteractors\(\[\],\s*\[(?<id>-?\d+),\]\)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex EnableInteractorsRx();

    /// <summary>
    /// Feed one raw log line through the state machine. Idempotent for
    /// unrelated lines — the common case is a quick set of substring checks
    /// that don't touch state.
    /// </summary>
    public void Observe(RawLogLine raw) => Observe(raw.Line, raw.Timestamp);

    public void Observe(string line, DateTime timestamp)
    {
        // 1. Interaction start — always begins a fresh bracket, replacing any prior.
        if (_interactionParser.TryParse(line, timestamp) is InteractionStartEvent start)
        {
            _state = State.InFlight;
            _bracketName = start.EntityName;
            _bracketStartTimestamp = start.Timestamp;
            _bracketInteractorId = start.InteractorId;
            _bracketHarvestVerb = null;
            return;
        }

        // Below this point, only events relevant when a bracket is in flight.
        if (_state == State.Idle) return;

        // 2. TalkScreen / PreTalkScreen → storage UI / NPC dialog. Discard.
        if (TalkScreenRx().IsMatch(line))
        {
            ResetIdle();
            return;
        }

        // 3. Cooldown rejection screen text → cache the duration, close bracket.
        if (_state == State.InFlight
            && _rejectionParser.TryParse(line, timestamp) is ChestCooldownObservedEvent rejection
            && _bracketName is not null)
        {
            _source.OnChestCooldownObserved(_bracketName, rejection.Duration);
            ResetIdle();
            return;
        }

        // 4. Interactor-bound delay loop → bracket is a harvest (Gather "Collecting Fruit..."
        // on a tree, etc.), not a chest. Stash the verb and let the bracket continue
        // — its closing signal will reset state, and the AddItem step will see a
        // non-null harvest verb and skip the chest commit. Self-targeted delay loops
        // (Eat / Drink / UseItem / UseTeleportationCircle) don't carry the
        // IsInteractorDelayLoop flag and shouldn't poison an unrelated bracket.
        if (_state == State.InFlight
            && _delayLoopParser.TryParse(line, timestamp) is InteractionDelayLoopEvent delay
            && delay.IsInteractor)
        {
            _bracketHarvestVerb = delay.Verb;
            return;
        }

        // 5. AddItem inside bracket → confirmed loot, *unless* a harvest verb has
        // been stashed for this bracket. No chest in any captured log emits
        // ProcessDoDelayLoop, so the discriminator is "delay-loop verb present →
        // not a chest"; the verb itself is captured for diagnostics and so we
        // can promote to an explicit allowlist if a chest ever shows up with one.
        if (_state == State.InFlight && AddItemRx().IsMatch(line) && _bracketName is not null)
        {
            if (_bracketHarvestVerb is null)
                _source.OnChestInteraction(_bracketName, _bracketStartTimestamp);
            _state = State.Committed;
            return;
        }

        // 6. EnableInteractors with matching id → bracket close.
        if (EnableInteractorsRx().Match(line) is { Success: true } m
            && long.TryParse(m.Groups["id"].Value, out var closingId)
            && closingId == _bracketInteractorId)
        {
            ResetIdle();
            return;
        }

        // 7. EndInteraction with matching id → bracket close (symmetric to
        // EnableInteractors). Portals close via this signal; without it the
        // bracket would sit InFlight long enough for an unrelated AddItem
        // to commit "Portal" as a chest.
        if (_endParser.TryParse(line, timestamp) is InteractionEndEvent end
            && end.InteractorId == _bracketInteractorId)
        {
            ResetIdle();
            return;
        }
    }

    private void ResetIdle()
    {
        _state = State.Idle;
        _bracketName = null;
        _bracketStartTimestamp = default;
        _bracketInteractorId = 0;
        _bracketHarvestVerb = null;
    }

    private enum State
    {
        Idle,
        InFlight,
        Committed,
    }
}
