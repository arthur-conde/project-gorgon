using System.Text.RegularExpressions;
using Gandalf.Parsing;
using Mithril.Shared.Logging;

namespace Gandalf.Services;

/// <summary>
/// Signal-driven state machine that distinguishes a loot-chest interaction
/// from a storage-vault, workstation, NPC-dialog, or summons interaction.
/// Replaces the v1 substring heuristic that filtered chest prefab names by
/// <c>Contains("StaticChest")</c> — that filter silently dropped
/// <c>EltibuleSecretChest</c> and would regress on every game patch that
/// introduces a new chest theme.
///
/// Bracket shapes (verified against captured Player.log):
/// <list type="bullet">
/// <item><b>Loot chest:</b> <c>ProcessStartInteraction → ProcessAddItem(...) → ProcessEnableInteractors([],[id,])</c></item>
/// <item><b>Storage UI:</b> <c>ProcessStartInteraction → ProcessShowStorageVault</c> (or <c>ProcessTalkScreen</c> for catalog-fronted access)</item>
/// <item><b>Workstation / teleport pad:</b> <c>ProcessStartInteraction → ProcessShowRecipes(&lt;skill&gt;)</c></item>
/// <item><b>Passcode-gated container:</b> <c>ProcessStartInteraction → ProcessInputBox</c></item>
/// <item><b>Cooldown rejection:</b> <c>ProcessStartInteraction → ProcessScreenText("You've already looted...")</c></item>
/// <item><b>Harvest (delay-loop):</b> <c>ProcessStartInteraction → ProcessDoDelayLoop(... IsInteractorDelayLoop) → ProcessAddItem</c></item>
/// <item><b>Harvest (wait-loop):</b> <c>ProcessStartInteraction → ProcessWaitInteraction(... "verb body") → ProcessAddItem</c></item>
/// </list>
/// Soft timeout (#174): if a bracket has been <c>InFlight</c> longer than
/// <see cref="SoftTimeout"/> with no positive signal, a subsequent
/// <c>ProcessAddItem</c> is treated as out-of-bracket. Backstop for
/// no-signal leakers like <c>SummonedFlowerN</c> and <c>SummonedHorseApple</c>
/// that emit only <c>ProcessUpdateDescription</c>.
/// </summary>
public sealed partial class LootBracketTracker
{
    /// <summary>
    /// A bracket older than this with no positive signal stops accepting
    /// <c>ProcessAddItem</c> commits. Captured real-chest brackets always
    /// fire AddItem within the same log second; 2 s is plenty of headroom
    /// while still catching the SummonedFlower / SummonedHorseApple /
    /// GoblinStew leakers where ambient AddItems land seconds later.
    /// </summary>
    public static readonly TimeSpan SoftTimeout = TimeSpan.FromSeconds(2);

    private readonly LootSource _source;
    private readonly ChestInteractionParser _interactionParser;
    private readonly ChestRejectionParser _rejectionParser;
    private readonly MilkingRejectionParser _milkingRejectionParser;
    private readonly InteractionEndParser _endParser;
    private readonly InteractionDelayLoopParser _delayLoopParser;
    private readonly InteractionWaitParser _waitParser;

    private State _state = State.Idle;
    private string? _bracketName;
    private DateTime _bracketStartTimestamp;
    private long _bracketInteractorId;
    private string? _bracketHarvestVerb;

    public LootBracketTracker(
        LootSource source,
        ChestInteractionParser interactionParser,
        ChestRejectionParser rejectionParser,
        MilkingRejectionParser milkingRejectionParser,
        InteractionEndParser endParser,
        InteractionDelayLoopParser delayLoopParser,
        InteractionWaitParser waitParser)
    {
        _source = source;
        _interactionParser = interactionParser;
        _rejectionParser = rejectionParser;
        _milkingRejectionParser = milkingRejectionParser;
        _endParser = endParser;
        _delayLoopParser = delayLoopParser;
        _waitParser = waitParser;
    }

    /// <summary>True iff the tracker is currently inside an interaction bracket.</summary>
    public bool IsInFlight => _state != State.Idle;

    /// <summary>
    /// Combined "this is a UI dialog, not loot" signal set. <c>TalkScreen</c>
    /// covers NPCs + catalog-fronted storage; <c>ShowStorageVault</c> covers
    /// direct storage-chest clicks (the universal storage UI signal carrying
    /// the storagevaults.json vault id); <c>ShowRecipes</c> covers
    /// workstations and teleport pads (Cooking / Tanning / Teleportation /
    /// etc.); <c>InputBox</c> covers passcode-gated containers like
    /// <c>IvynsChest</c>. Any of these inside an in-flight bracket discards
    /// the bracket without committing.
    /// </summary>
    [GeneratedRegex(
        """LocalPlayer:\s*Process(?:(?:Pre)?TalkScreen|ShowStorageVault|ShowRecipes|InputBox)\(""",
        RegexOptions.CultureInvariant)]
    private static partial Regex DialogDiscardRx();

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

        // 2. Any UI dialog signal (TalkScreen / ShowStorageVault / ShowRecipes /
        // InputBox) → not loot. Discard.
        if (DialogDiscardRx().IsMatch(line))
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

        // 3b. Cow milking cooldown rejection — different log channel
        // (ErrorMessage vs GeneralInfo) and different grammar (relative-past
        // "in the past hour" vs forward-looking "will refill N hours after").
        // Same downstream contract as the chest rejection: cache the duration
        // by the bracket's internal name, close the bracket. Gated by the
        // "Cow_" name prefix so a stray ErrorMessage from a non-cow bracket
        // (e.g. "You're too encumbered!" inside an unrelated chest bracket)
        // doesn't poison the chest's cooldown by 1 hour. Wiki: #181.
        if (_state == State.InFlight
            && _bracketName is not null
            && _bracketName.StartsWith("Cow_", StringComparison.Ordinal)
            && _milkingRejectionParser.TryParse(line, timestamp) is ChestCooldownObservedEvent milkRejection)
        {
            _source.OnChestCooldownObserved(_bracketName, milkRejection.Duration);
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

        // 5. Interactor-bound wait loop with non-empty body → harvest-style
        // suppression, parallel to the delay-loop branch. Empty-body variant
        // (the IvynsChest unlock animation) is a no-op — its bracket gets
        // discarded by the storage-vault signal that follows. Id-matched so
        // a stray wait from a different interactor can't poison the bracket.
        if (_state == State.InFlight
            && _waitParser.TryParse(line, timestamp) is InteractionWaitEvent wait
            && wait.InteractorId == _bracketInteractorId
            && !string.IsNullOrEmpty(wait.Body))
        {
            _bracketHarvestVerb = "Wait";
            return;
        }

        // 6. AddItem inside bracket → confirmed loot, *unless* a harvest verb has
        // been stashed for this bracket, OR the bracket has been InFlight longer
        // than SoftTimeout (the AddItem isn't from this bracket; it's an ambient
        // event that landed on a no-positive-signal leaker like SummonedFlower).
        // No chest in any captured log emits ProcessDoDelayLoop or
        // ProcessWaitInteraction, so the discriminator is "harvest verb present
        // → not a chest"; the verb itself is captured for diagnostics.
        if (_state == State.InFlight && AddItemRx().IsMatch(line) && _bracketName is not null)
        {
            var elapsed = timestamp - _bracketStartTimestamp;
            if (elapsed > SoftTimeout)
            {
                ResetIdle();
                return;
            }
            if (_bracketHarvestVerb is null)
                _source.OnChestInteraction(_bracketName, _bracketStartTimestamp);
            _state = State.Committed;
            return;
        }

        // 7. EnableInteractors with matching id → bracket close.
        if (EnableInteractorsRx().Match(line) is { Success: true } m
            && long.TryParse(m.Groups["id"].Value, out var closingId)
            && closingId == _bracketInteractorId)
        {
            ResetIdle();
            return;
        }

        // 8. EndInteraction with matching id → bracket close (symmetric to
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
