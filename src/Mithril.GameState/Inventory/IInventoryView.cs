using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Canonical inventory surface for modules (#602). Composes the PlayerWorld
/// instance-id ledger (via <see cref="IPlayerInventoryState"/>) with the
/// ChatWorld name-keyed stack-size observations (via
/// <see cref="IChatInventoryState"/>), pairing them with a TTL-windowed
/// correlator keyed on <c>(InternalName, Server, Character)</c>.
///
/// <para><b>Canonical surface = typed-frame bus.</b> Subscribe via
/// <see cref="Bus"/> for any of the three view-emitted typed events
/// — <see cref="InventoryItemAdded"/>, <see cref="InventoryItemRemoved"/>,
/// <see cref="InventoryStackChanged"/>. Consumers post-migration use the bus.
/// The legacy <see cref="Subscribe(Action{InventoryEvent}, Mithril.Shared.Logging.ReplayMode)"/>
/// shim translates these typed frames back into the union-shaped
/// <c>InventoryEvent</c> for the six pre-existing consumers (Arwen, Samwise,
/// Palantir, Legolas, Saruman, Motherlode) — migrations of each are tracked
/// in <a href="https://github.com/moumantai-gg/mithril/issues/659">#659</a>.</para>
///
/// <para><b>Why a view, not a service.</b> Pre-#602 <c>IInventoryService</c>
/// consumed both Player.log AND chat directly. That violates the world-sim
/// principle 3 — no service spans both sources. The split puts each source
/// in its own world; cross-source composition lives in this view layer (per
/// principle 4 + §Worked example 1 of <c>docs/world-simulator.md</c>).</para>
/// </summary>
public interface IInventoryView
{
    /// <summary>
    /// The view's typed-frame bus. Subscribe via
    /// <c>Bus.Subscribe&lt;InventoryItemAdded&gt;(...)</c> /
    /// <c>InventoryItemRemoved</c> / <c>InventoryStackChanged</c> for the
    /// canonical post-migration surface.
    /// </summary>
    IWorldEventBus Bus { get; }

    /// <summary>
    /// Resolve an instance id to its <c>InternalName</c> via the PlayerWorld
    /// ledger (passthrough of <see cref="IPlayerInventoryState.TryResolve"/>
    /// — retained entries survive deletion).
    /// </summary>
    bool TryResolve(long instanceId, out string internalName);

    /// <summary>
    /// Resolve an instance id to its current stack size, if and only if the
    /// size has been confirmed by an authoritative source (chat correlation,
    /// non-stackable reference data, export seed, <c>ProcessUpdateItemCode</c>,
    /// <c>ProcessRemoveFromStorageVault</c>). The legacy contract — preserved
    /// verbatim for the pre-#602 consumers.
    /// </summary>
    bool TryGetStackSize(long instanceId, out int stackSize);

    /// <summary>
    /// Legacy union-shaped event subscription. The view translates its typed
    /// frames into <see cref="InventoryEvent"/> for backward compatibility
    /// with the six pre-#602 consumers; new code subscribes via <see cref="Bus"/>
    /// directly.
    /// </summary>
    /// <remarks>
    /// The default <see cref="Mithril.Shared.Logging.ReplayMode.FromSessionStart"/>
    /// replays the full in-session event log atomically before going live,
    /// closing the late-subscribe race the pre-split service surfaced in #585.
    /// </remarks>
    [Obsolete("Subscribe to Frame<InventoryItemAdded> et al. on IInventoryView.Bus")]
    IDisposable Subscribe(
        Action<InventoryEvent> handler,
        Mithril.Shared.Logging.ReplayMode replay = Mithril.Shared.Logging.ReplayMode.FromSessionStart);
}
