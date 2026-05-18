using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Player started any interaction (chest, vendor, storage vault, NPC dialog).
/// The bracket discriminator — loot vs storage vs NPC — is signal-driven in
/// <c>LootBracketTracker</c>: <c>ProcessAddItem</c> inside the bracket means
/// loot; <c>ProcessTalkScreen</c> means UI dialog (storage / NPC); the bracket
/// closes on <c>ProcessEnableInteractors</c>. The parser is intentionally
/// broad — naming heuristics like "*StaticChest*" silently dropped legitimate
/// loot prefabs (e.g. EltibuleSecretChest), so the filter moved from the
/// parser to the state machine.
/// </summary>
public sealed record InteractionStartEvent(DateTime Timestamp, long InteractorId, string EntityName)
    : LogEvent(Timestamp);

/// <summary>
/// Player tried a chest already on cooldown — game emits the duration in the
/// rejection screen text. Updates the catalog cache so future first-time loots
/// of the same template start with the right duration. The chest's internal
/// name isn't carried on the rejection line itself; the bracket tracker
/// correlates the rejection to the in-flight interaction.
/// </summary>
public sealed record ChestCooldownObservedEvent(DateTime Timestamp, string ChestInternalName, TimeSpan Duration)
    : LogEvent(Timestamp);

/// <summary>
/// Player ended an interaction. Symmetric close to <c>ProcessEnableInteractors</c>:
/// in live captures, portals close via <c>ProcessEndInteraction(id)</c> and
/// chests close via <c>ProcessEnableInteractors([], [id,])</c>; the tracker
/// needs to honor both so non-chest brackets don't sit InFlight long enough
/// for an unrelated <c>ProcessAddItem</c> to commit them as a chest.
/// </summary>
public sealed record InteractionEndEvent(DateTime Timestamp, long InteractorId)
    : LogEvent(Timestamp);

/// <summary>
/// Player started an action with a delay loop (gather fruit, eat food, recall
/// to a teleport circle, distill, etc.). The discriminator for the loot
/// tracker is <see cref="IsInteractor"/>: when set, the delay loop is bound
/// to the in-flight interaction (e.g. <c>Gather, "Collecting Fruit..."</c> on
/// a tree), and the subsequent <c>ProcessAddItem</c> is harvested loot, not a
/// chest. Self-targeted delay loops (<c>Eat</c>, <c>Drink</c>, <c>UseItem</c>,
/// <c>UseTeleportationCircle</c>) don't carry the flag and shouldn't poison
/// an unrelated bracket. The verb is captured for diagnostics and so we can
/// promote to an explicit allowlist if a chest ever emits one.
/// </summary>
public sealed record InteractionDelayLoopEvent(DateTime Timestamp, string Verb, bool IsInteractor)
    : LogEvent(Timestamp);

/// <summary>
/// Interactor-bound progress loop — sibling to <see cref="InteractionDelayLoopEvent"/>
/// but a different signal entirely. Fires for activities like filling water
/// bottles at a well, fishing, and (with empty body) the unlock animation
/// for passcode-gated storage chests. A non-empty <see cref="Body"/> means
/// the loop is producing items (the body is the user-facing progress
/// description, e.g. <c>"Filling Water Bottles..."</c>); the bracket tracker
/// treats that as harvest-style suppression so the subsequent
/// <c>ProcessAddItem</c> doesn't commit a chest row.
/// </summary>
public sealed record InteractionWaitEvent(DateTime Timestamp, long InteractorId, string Body)
    : LogEvent(Timestamp);
