using Arda.Abstractions.Logs;

namespace Arda.Composition.Events;

/// <summary>
/// Published by the <c>NpcStateComposer</c> when a single NPC's record is updated
/// (favor change, vendor gold observation, or favor tier change).
/// </summary>
public readonly record struct NpcStateChanged(string NpcKey, NpcRecord Record, LogLineMetadata Metadata);
