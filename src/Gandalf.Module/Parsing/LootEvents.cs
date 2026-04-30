using Mithril.Shared.Logging;

namespace Gandalf.Parsing;

/// <summary>
/// Player opened (started interaction with) a static chest. Anchor for the
/// cooldown clock — <c>StartedAt</c> on the resulting timer is this Timestamp.
/// </summary>
public sealed record ChestInteractionEvent(DateTime Timestamp, string ChestInternalName)
    : LogEvent(Timestamp);

/// <summary>
/// Player tried a chest already on cooldown — game emits the duration in the
/// rejection screen text. Updates the catalog cache so future first-time loots
/// of the same template start with the right duration.
/// </summary>
public sealed record ChestCooldownObservedEvent(DateTime Timestamp, string ChestInternalName, TimeSpan Duration)
    : LogEvent(Timestamp);

/// <summary>
/// Player earned a kill credit on a reward-cooldown creature. v1 anchors the
/// cooldown on this line; the "reduced rewards" line that distinguishes a
/// rewarded kill from a cooldown-suppressed one is **Verification owed** per the
/// wiki and will refine this trigger when captured.
/// </summary>
public sealed record DefeatRewardEvent(DateTime Timestamp, string NpcDisplayName)
    : LogEvent(Timestamp);
