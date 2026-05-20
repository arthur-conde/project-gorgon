namespace Mithril.Shared.Logging;

/// <summary>
/// L1 per-subscription replay policy (#511 deliverable 3 / #550 capability A).
/// Per-subscription, not per-module — one consumer class may hold
/// subscriptions of different modes (Legolas: its area-bridge subscription
/// wants replay; its survey-dispatch subscription wants live-only).
///
/// <para>For chat-backed subscriptions the value is structurally moot —
/// <c>IChatLogStream</c> has no backlog by construction (the per-day file
/// directory is seeded to current-end before the first emission). The
/// driver accepts any value on chat subscriptions for API uniformity,
/// treats them all as <see cref="LiveOnly"/>, and emits a diagnostic on
/// the first mismatch so the spurious knob doesn't go unnoticed in code
/// review.</para>
/// </summary>
public enum ReplayMode
{
    /// <summary>
    /// Backlog from L0 session start delivered first (each envelope carries
    /// <c>IsReplay = true</c>), then live (<c>IsReplay = false</c>). The
    /// existing implicit default of every consumer pre-L1 — non-breaking.
    /// </summary>
    FromSessionStart = 0,

    /// <summary>
    /// Skip the replay drain entirely; only emissions arriving after the
    /// subscription is established are delivered, all with <c>IsReplay = false</c>.
    /// </summary>
    LiveOnly = 1,

    /// <summary>
    /// Currently behaviourally identical to <see cref="LiveOnly"/>; reserved
    /// for future replay-window variants (e.g. "since timestamp T", "since
    /// sequence S without persisted high-water"). Distinct value so consumers
    /// can express intent — Saruman/Discovery wants "since this gate-open"
    /// not "skip backlog forever," and a future revision may differentiate.
    /// </summary>
    SinceSubscribe = 2,
}
