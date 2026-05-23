using Mithril.Shared.Logging;

namespace Mithril.GameState.Effects;

/// <summary>
/// Live set of effects active on the local player, parsed from
/// <c>Player.log</c>'s <c>ProcessAddEffects</c> / <c>ProcessRemoveEffects</c>
/// / <c>ProcessUpdateEffectName</c> verbs. Player-only for v1 — non-local
/// targets are out of scope (see issue #590 "Scope note").
///
/// <para><b>Three channels (per <a href="https://github.com/moumantai-gg/mithril/pull/584">#584</a>).</b>
/// <list type="bullet">
///   <item><b>Query</b> — <see cref="TryGet"/> for a single catalog id plus
///   <see cref="ActiveEffects"/> for the full snapshot. Point-in-time;
///   readers iterate the dict for UI seeding.</item>
///   <item><b>React</b> — <see cref="Subscribe"/> delivers the full
///   <see cref="EffectEvent"/> stream. Default
///   <see cref="ReplayMode.FromSessionStart"/> atomically replays every
///   Added / Removed / DisplayNameChanged event in log order before live
///   dispatch begins (the post-#585 late-attach atomic-replay contract).
///   <see cref="ReplayMode.LiveOnly"/> skips the replay.</item>
///   <item><b>Bind</b> — deferred. A full
///   <c>IReadOnlyObservableCollection&lt;EffectState&gt;</c> with
///   <c>INotifyCollectionChanged</c> for direct WPF binding is part of the
///   service-side Bind-surface lift initiative (#584 follow-up). Consumers
///   needing observable mutation today seed off <see cref="ActiveEffects"/>
///   + subscribe via <see cref="Subscribe"/> and rebuild their own
///   observable surface — the same wrap-a-Subscribe pattern Palantir used
///   over the inventory service pre-#726.</item>
/// </list>
/// </para>
///
/// <para><b>Identity model.</b> The live set is keyed by effect
/// <see cref="EffectState.CatalogId"/> (small ints from <c>effects.json</c>).
/// Instance ids (large per-application ints from
/// <c>ProcessRemoveEffects</c> / <c>ProcessUpdateEffectName</c>) are
/// recorded on a best-effort basis when a same-timestamp Update names an
/// entry; the catalog-id-only majority cannot be removed by id and lingers
/// in <see cref="ActiveEffects"/> until the next snapshot replay reconciles
/// them. This is consistent with PG's tendency to dynamically name the
/// effect subset where expiration matters (food buffs, level-tagged
/// effects); the unnamed majority (equipment bonuses, character traits) are
/// genuinely permanent, so lingering reflects the game state correctly.
/// See issue #590 "Known unknowns" for the catalog-id / instance-id bridge
/// gap.</para>
/// </summary>
public interface IPlayerEffectsStateService
{
    /// <summary>
    /// Resolve a catalog id to its current <see cref="EffectState"/>, if and
    /// only if the effect is currently active on the local player. Returns
    /// <c>false</c> for effects that have been removed (entries are not
    /// retained post-removal — unlike <see cref="Mithril.GameState.Inventory.IInventoryService.TryResolve"/>,
    /// no late-lookup path needs the pre-removal state).
    /// </summary>
    bool TryGet(int catalogId, out EffectState state);

    /// <summary>
    /// Point-in-time snapshot of every catalog id currently active on the
    /// local player. Returned dictionary is a defensive copy — safe to
    /// enumerate without holding service-internal locks. For long-lived
    /// observable bindings, prefer <see cref="Subscribe"/>.
    /// </summary>
    IReadOnlyDictionary<int, EffectState> ActiveEffects { get; }

    /// <summary>
    /// Register an <see cref="EffectEvent"/> handler. With the default
    /// <see cref="ReplayMode.FromSessionStart"/>, the service atomically
    /// replays every event in its internal event log (Added / Removed /
    /// DisplayNameChanged from session start, in original order) to the
    /// new handler before adding it to the live-dispatch list — late
    /// subscribers see the same history as one attached at startup. With
    /// <see cref="ReplayMode.LiveOnly"/>, the replay is skipped and only
    /// events arriving after the call are delivered.
    ///
    /// <para>The handler is invoked synchronously under an internal lock
    /// both during replay (on the subscribing thread) and during live
    /// dispatch (on the L1 pump thread). Subscribers doing non-trivial work
    /// should marshal off-thread immediately.</para>
    ///
    /// <para>Dispose the returned token to stop receiving further events.</para>
    /// </summary>
    IDisposable Subscribe(
        Action<EffectEvent> handler,
        ReplayMode replay = ReplayMode.FromSessionStart);
}
