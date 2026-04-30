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
