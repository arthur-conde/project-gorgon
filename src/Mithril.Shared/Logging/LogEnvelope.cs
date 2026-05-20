namespace Mithril.Shared.Logging;

/// <summary>
/// L1 (#511 deliverable 3 / #550) delivery envelope. Wraps a typed log
/// payload — <see cref="LocalPlayerLogLine"/>, <see cref="CombatActorLogLine"/>,
/// <see cref="SystemSignalLogLine"/>, or <see cref="RawLogLine"/> (chat) —
/// with the <see cref="IsReplay"/> bit that distinguishes the whole-backlog
/// drain at subscription time from the live-tail that follows.
///
/// <para><b><see cref="IsReplay"/></b> is the L1-side companion of the
/// Tier-3 contract in <c>docs/cross-source-correlation.md</c>:
/// <c>ReadMonotonicTicks</c> is a usable cross-source tiebreaker within a
/// shared game-second <em>only when</em> <c>IsReplay == false</c>. The flag
/// is also the structural enabler for Divergence-2 (Legolas-style)
/// mixed-handler subscriptions — one subscription serves both a
/// replay-needing handler (area bridge) and a live-only handler (survey
/// dispatch); the consumer per-handler-drops on <c>IsReplay == true</c>
/// rather than maintaining two separate subscriptions.</para>
///
/// <para>The flag flips deterministically: every envelope yielded from
/// the upstream's session-replay snapshot carries <c>IsReplay == true</c>;
/// from the first live channel emission onward, <c>IsReplay == false</c>.
/// The L0.5 router answers this authoritatively via the L1-facing
/// <c>SubscribeWithReplayMarkerAsync</c> method on each typed pipe (added
/// in #550 PR 1) — no "is this still the first batch?" heuristic, no
/// sync-vs-async timing inference.</para>
/// </summary>
public readonly record struct LogEnvelope<T>(T Payload, bool IsReplay) where T : class;
