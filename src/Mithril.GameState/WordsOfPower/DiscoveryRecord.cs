namespace Mithril.GameState.WordsOfPower;

/// <summary>
/// Per-character Word-of-Power discovery record (#603 — Player.log half).
/// Captures the once-and-forever-known facts about a discovered code: its
/// effect name + description (as the player saw them on discovery), and the
/// timestamp it was first observed. Spent-state is NOT part of this record —
/// monotonic Spent computation is the view layer's concern (see
/// <see cref="WordOfPowerView"/>).
/// </summary>
public sealed class DiscoveryRecord
{
    public required string Code { get; init; }
    public required string EffectName { get; set; }
    public required string Description { get; set; }
    public required DateTime DiscoveredAt { get; init; }
}
