using Arda.Abstractions.Logs;

namespace Arda.World.Player.Events;

/// <summary>
/// Tier 2 passthrough for <c>ProcessScreenText</c> lines containing ErrorMessage.
/// Marker event with no payload — the occurrence itself is the signal.
/// </summary>
public readonly record struct ScreenTextErrorFrame(LogLineMetadata Metadata);
