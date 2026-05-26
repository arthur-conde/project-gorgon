using Arda.World.Player.Events;

namespace Gandalf.Services;

/// <summary>
/// Signal-driven state machine that distinguishes a loot-chest interaction
/// from a storage-vault, workstation, NPC-dialog, or summons interaction.
/// Replaces the v1 substring heuristic that filtered chest prefab names by
/// <c>Contains("StaticChest")</c> — that filter silently dropped
/// <c>EltibuleSecretChest</c> and would regress on every game patch that
/// introduces a new chest theme.
///
/// Post-Arda migration: consumes typed domain events forwarded by
/// <see cref="LootIngestionService"/> instead of regex-matching raw log
/// lines. The FSM semantics are identical — only the input surface changed.
///
/// Bracket shapes (verified against captured Player.log):
/// <list type="bullet">
/// <item><b>Loot chest:</b> <c>InteractionStarted → InventoryItemAdded → EnableInteractorsFrame</c></item>
/// <item><b>Storage UI:</b> <c>InteractionStarted → TalkScreenFrame</c> (or vendor/catalog)</item>
/// <item><b>Cooldown rejection:</b> <c>InteractionStarted → ScreenTextObserved("You've already looted...")</c></item>
/// <item><b>Harvest (delay-loop):</b> <c>InteractionStarted → DelayLoopStarted(IsInteractor) → InventoryItemAdded</c></item>
/// <item><b>Harvest (wait-loop):</b> <c>InteractionStarted → InteractionWaiting("verb body") → InventoryItemAdded</c></item>
/// </list>
/// Soft timeout (#174): if a bracket has been <c>InFlight</c> longer than
/// <see cref="SoftTimeout"/> with no positive signal, a subsequent
/// <c>InventoryItemAdded</c> is treated as out-of-bracket. Backstop for
/// no-signal leakers like <c>SummonedFlowerN</c> and <c>SummonedHorseApple</c>
/// that emit only <c>ProcessUpdateDescription</c>.
/// </summary>
public sealed class LootBracketTracker
{
    /// <summary>
    /// A bracket older than this with no positive signal stops accepting
    /// <c>InventoryItemAdded</c> commits. Captured real-chest brackets always
    /// fire AddItem within the same log second; 2 s is plenty of headroom
    /// while still catching the SummonedFlower / SummonedHorseApple /
    /// GoblinStew leakers where ambient AddItems land seconds later.
    /// </summary>
    public static readonly TimeSpan SoftTimeout = TimeSpan.FromSeconds(2);

    private readonly LootSource _source;

    private State _state = State.Idle;
    private string? _bracketName;
    private DateTime _bracketStartTimestamp;
    private long _bracketInteractorId;
    private string? _bracketHarvestVerb;

    public LootBracketTracker(LootSource source)
    {
        _source = source;
    }

    /// <summary>True iff the tracker is currently inside an interaction bracket.</summary>
    public bool IsInFlight => _state != State.Idle;

    /// <summary>
    /// 1. Interaction start — always begins a fresh bracket, replacing any prior.
    /// </summary>
    public void OnInteractionStarted(InteractionStarted evt)
    {
        _state = State.InFlight;
        _bracketName = evt.Name;
        _bracketStartTimestamp = evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow;
        _bracketInteractorId = evt.EntityId;
        _bracketHarvestVerb = null;
    }

    /// <summary>
    /// 2. TalkScreen signal — "this is a UI dialog, not loot". Discard bracket.
    /// Covers NPCs + catalog-fronted storage.
    /// </summary>
    public void OnTalkScreen()
    {
        if (_state == State.Idle) return;
        ResetIdle();
    }

    /// <summary>
    /// 3. Cooldown rejection via ScreenText — handles both chest rejection
    /// (GeneralInfo channel) and cow milking rejection (ErrorMessage channel).
    /// The Arda ScreenTextHandler emits <see cref="ScreenTextObserved"/> for
    /// ALL categories including ErrorMessage, so both paths arrive here.
    /// </summary>
    public void OnScreenTextObserved(ScreenTextObserved evt)
    {
        if (_state != State.InFlight) return;
        if (_bracketName is null) return;

        var text = evt.Text.Span;
        var category = evt.Category.Span;
        var ts = evt.Metadata.Timestamp?.UtcDateTime ?? DateTime.UtcNow;

        // 3a. Chest cooldown rejection (GeneralInfo channel)
        if (category.SequenceEqual("GeneralInfo")
            && TryParseChestRejection(text, out var chestDuration))
        {
            _source.OnChestCooldownObserved(_bracketName, chestDuration, ts);
            ResetIdle();
            return;
        }

        // 3b. Cow milking cooldown rejection (ErrorMessage channel).
        // Gated by the "Cow_" name prefix so a stray ErrorMessage from a
        // non-cow bracket doesn't poison the chest's cooldown.
        if (category.SequenceEqual("ErrorMessage")
            && _bracketName.StartsWith("Cow_", StringComparison.Ordinal)
            && TryParseMilkingRejection(text, out var milkDuration))
        {
            _source.OnChestCooldownObserved(
                _bracketName, milkDuration, ts, anchorFromRejection: true);
            ResetIdle();
        }
    }

    /// <summary>
    /// 4. Interactor-bound delay loop → bracket is a harvest, not a chest.
    /// Self-targeted delay loops (Eat / Drink / UseItem) don't carry the
    /// IsInteractorDelayLoop flag and shouldn't poison an unrelated bracket.
    /// </summary>
    public void OnDelayLoopStarted(DelayLoopStarted evt)
    {
        if (_state != State.InFlight) return;
        if (!evt.IsInteractorDelayLoop) return;
        _bracketHarvestVerb = evt.Verb.ToString();
    }

    /// <summary>
    /// 5. Interactor-bound wait loop with non-empty body → harvest-style suppression.
    /// Empty-body variant (the IvynsChest unlock animation) is a no-op.
    /// Id-matched so a stray wait from a different interactor can't poison the bracket.
    /// </summary>
    public void OnInteractionWaiting(InteractionWaiting evt)
    {
        if (_state != State.InFlight) return;
        if (evt.EntityId != _bracketInteractorId) return;
        if (evt.Body.Length == 0) return;
        _bracketHarvestVerb = "Wait";
    }

    /// <summary>
    /// 6. AddItem inside bracket → confirmed loot, unless a harvest verb has
    /// been stashed or the bracket has exceeded the soft timeout.
    /// </summary>
    public void OnInventoryItemAdded(DateTime timestamp)
    {
        if (_state != State.InFlight) return;
        if (_bracketName is null) return;

        var elapsed = timestamp - _bracketStartTimestamp;
        if (elapsed > SoftTimeout)
        {
            ResetIdle();
            return;
        }
        if (_bracketHarvestVerb is null)
            _source.OnChestInteraction(_bracketName, _bracketStartTimestamp);
        _state = State.Committed;
    }

    /// <summary>
    /// 7. EnableInteractors with matching id → bracket close.
    /// </summary>
    public void OnEnableInteractors(EnableInteractorsFrame evt)
    {
        if (_state == State.Idle) return;
        if (evt.InteractorId != _bracketInteractorId) return;
        ResetIdle();
    }

    /// <summary>
    /// 8. EndInteraction with matching id → bracket close.
    /// Portals close via this signal; without it the bracket would sit
    /// InFlight long enough for an unrelated AddItem to commit.
    /// </summary>
    public void OnInteractionEnded(InteractionEnded evt)
    {
        if (_state == State.Idle) return;
        if (evt.EntityId != _bracketInteractorId) return;
        ResetIdle();
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

    // ── Inline rejection parsers (moved from dedicated parser classes) ──────
    // These are intentionally inlined because the bracket tracker is the only
    // consumer, and the Arda event already delivers the text as a span.

    internal static bool TryParseChestRejection(ReadOnlySpan<char> text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        const string prefix = "You've already looted this chest! (It will refill ";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var remainder = text[prefix.Length..];
        var spaceIdx = remainder.IndexOf(' ');
        if (spaceIdx <= 0) return false;

        if (!int.TryParse(remainder[..spaceIdx], out var value)) return false;
        var afterValue = remainder[(spaceIdx + 1)..];

        if (afterValue.StartsWith("minute", StringComparison.Ordinal))
            duration = TimeSpan.FromMinutes(value);
        else if (afterValue.StartsWith("hour", StringComparison.Ordinal))
            duration = TimeSpan.FromHours(value);
        else if (afterValue.StartsWith("day", StringComparison.Ordinal))
            duration = TimeSpan.FromDays(value);
        else
            return false;

        return true;
    }

    internal static bool TryParseMilkingRejection(ReadOnlySpan<char> text, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        const string marker = "You've already milked";
        if (!text.StartsWith(marker, StringComparison.Ordinal)) return false;

        const string pastMarker = "in the past ";
        var pastIdx = text.IndexOf(pastMarker, StringComparison.Ordinal);
        if (pastIdx < 0) return false;

        var remainder = text[(pastIdx + pastMarker.Length)..];
        // Try numeric form: "30 minutes." or singular form: "hour."
        var dotIdx = remainder.IndexOf('.');
        if (dotIdx <= 0) return false;
        var body = remainder[..dotIdx]; // e.g. "hour" or "30 minutes"

        var spaceIdx = body.IndexOf(' ');
        int value;
        ReadOnlySpan<char> unit;
        if (spaceIdx > 0)
        {
            if (!int.TryParse(body[..spaceIdx], out value)) return false;
            unit = body[(spaceIdx + 1)..];
        }
        else
        {
            value = 1;
            unit = body;
        }

        if (unit.StartsWith("minute", StringComparison.Ordinal))
            duration = TimeSpan.FromMinutes(value);
        else if (unit.StartsWith("hour", StringComparison.Ordinal))
            duration = TimeSpan.FromHours(value);
        else if (unit.StartsWith("day", StringComparison.Ordinal))
            duration = TimeSpan.FromDays(value);
        else
            return false;

        return true;
    }
}
