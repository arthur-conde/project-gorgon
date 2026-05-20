namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532): a Player.log line classified as <c>[ts] entity_&lt;id&gt;: On*(…)</c>
/// — a combat-actor event where a mob/entity registers being hit by the local
/// player's ability. Corpus shape: <c>OnAttackHitMe(&lt;ability&gt;). Evaded = &lt;bool&gt;</c>,
/// though the verb space is open-ended.
///
/// <para>The <c>entity_&lt;id&gt;:</c> envelope is eaten exactly the same way
/// <see cref="LocalPlayerLogLine"/> eats <c>LocalPlayer:</c>; the parsed
/// <see cref="EntityId"/> is surfaced separately so combat consumers don't
/// re-parse it. <see cref="Data"/> is the bare <c>OnVerb(args)</c>.</para>
///
/// <para><b>Reserved pipe.</b> No consumer subscribes today. Per #532, the
/// classifier still routes these lines distinctly (not to the cheap-discard
/// bucket) so a future combat consumer slots in without moving the channel
/// boundary. A token-prefix shortcut filing all <c>entity_</c> lines into
/// discard would silently bleed this signal — see the
/// <c>entity_&lt;id&gt;_skin</c> / <c>Not on nav mesh!</c> regression guards
/// in the per-rule fixtures.</para>
/// </summary>
public sealed record CombatActorLogLine(
    DateTimeOffset Timestamp,
    long EntityId,
    string Data,
    long Sequence,
    long ReadMonotonicTicks,
    string? Raw = null);
