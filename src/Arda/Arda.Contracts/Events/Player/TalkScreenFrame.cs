using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough marker for <c>ProcessTalkScreen</c>. Signals a UI dialog
/// interaction (storage, NPC dialog). Primary consumer: Gandalf (LootBracketTracker
/// bracket discrimination — presence of TalkScreen inside a bracket means "not loot").
/// </summary>
public readonly record struct TalkScreenFrame(LogLineMetadata Metadata);
