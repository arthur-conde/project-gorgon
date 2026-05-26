namespace Arda.World.Player;

/// <summary>
/// Enriched skill snapshot with measurement timestamp. Wraps the raw
/// <see cref="SkillEntry"/> values from the log line.
/// </summary>
public readonly record struct SkillSnapshot(
    int Raw,
    int Bonus,
    int Xp,
    int Tnl,
    int Max,
    DateTimeOffset MeasuredAt)
{
    /// <summary>Effective level (Raw + Bonus).</summary>
    public int EffectiveLevel => Raw + Bonus;

    /// <summary>Whether the skill is at its maximum raw level.</summary>
    public bool IsCapped => Raw >= Max && Max > 0;
}

/// <summary>
/// Read-only view of the player's skill state. Enriched over
/// <see cref="IPlayerState"/> with timestamped atomic snapshots.
/// Consumers needing change notifications subscribe to
/// <see cref="Events.SkillUpdated"/> or <see cref="Events.SkillsLoaded"/>
/// via <see cref="Arda.Dispatch.IDomainEventSubscriber"/>.
/// </summary>
public interface ISkillState
{
    /// <summary>
    /// Atomic copy-on-write skill dictionary keyed by interned skill type key.
    /// The reference is replaced on each mutation for safe snapshot reads.
    /// </summary>
    IReadOnlyDictionary<string, SkillSnapshot> Skills { get; }
}
