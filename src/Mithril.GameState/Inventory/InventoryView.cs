using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Canonical inventory surface for modules (#602). Exposes both the typed-frame
/// bus (the post-migration canonical surface — <see cref="Bus"/> carrying
/// <see cref="InventoryItemAdded"/> / <see cref="InventoryItemRemoved"/> /
/// <see cref="InventoryStackChanged"/>) AND the legacy union-shaped
/// <c>Subscribe(Action&lt;InventoryEvent&gt;)</c> shim that the six pre-#602
/// consumers (Arwen, Samwise, Palantir, Legolas, Saruman, Motherlode)
/// currently subscribe through.
///
/// <para><b>Why this exists.</b> Pre-#602, <c>InventoryService</c> consumed
/// both Player.log and chat directly — violating the world-sim's principle 3
/// (no service spans both sources). The new architecture splits source
/// consumption into two per-world folders (<see cref="PlayerInventoryStateService"/>
/// in PlayerWorld; <see cref="ChatInventoryStateService"/> in ChatWorld);
/// cross-source composition lives in this view layer (per principle 4 +
/// §Worked example 1 of <c>docs/world-simulator.md</c>).</para>
///
/// <para><b>Implementation note: dual-surface bridge.</b> This view bridges
/// the typed-frame bus surface (new architectural canon) and the legacy
/// union-shaped <c>IInventoryService.Subscribe(Action&lt;InventoryEvent&gt;)</c>
/// surface (back-compat for the six pre-existing consumers). The two surfaces
/// share a single source of truth — the <c>InventoryService</c> implementation,
/// which keeps its existing L1-direct consumption for this PR's scope. The
/// view translates each emitted <see cref="InventoryEvent"/> into its typed
/// <see cref="Frame{T}"/> counterpart and publishes on <see cref="Bus"/>. New
/// code subscribes via the bus; the six pre-#602 consumers continue via the
/// shim; migration of each is tracked in
/// <a href="https://github.com/moumantai-gg/mithril/issues/659">#659</a>.</para>
///
/// <para><b>What this PR delivers.</b> The new architectural primitives
/// (<see cref="IPlayerInventoryState"/> + <see cref="IChatInventoryState"/>
/// folders, their world-bus change events, the view's typed bus surface) are
/// in place and wired into the two worlds. <c>InventoryService</c> remains
/// the consumed implementation backing the six pre-existing consumers'
/// behaviour — pristine semantics, no regressions. Follow-on PRs under #659
/// rewire each consumer to consume the new typed surfaces and (last) retire
/// the legacy <c>InventoryService</c> consumption path.</para>
/// </summary>
public sealed class InventoryView : IInventoryView, IDisposable
{
    private readonly IInventoryService _backing;
    private readonly IDiagnosticsSink? _diag;
    private readonly ViewEventBus _bus = new();
    private readonly IDisposable _shimSubscription;
    private bool _disposed;

    public InventoryView(IInventoryService backing, IDiagnosticsSink? diag = null)
    {
        _backing = backing ?? throw new ArgumentNullException(nameof(backing));
        _diag = diag;

        // The view's bus is the canonical post-migration surface; it must
        // observe every event the backing service emits, including the
        // event-log replay the backing service performs atomically under
        // its lock when this subscription attaches.
        _shimSubscription = _backing.Subscribe(OnBackingEvent, ReplayMode.FromSessionStart);
    }

    public IWorldEventBus Bus => _bus;

    public bool TryResolve(long instanceId, out string internalName)
        => _backing.TryResolve(instanceId, out internalName);

    public bool TryGetStackSize(long instanceId, out int stackSize)
        => _backing.TryGetStackSize(instanceId, out stackSize);

    public IDisposable Subscribe(
        Action<InventoryEvent> handler,
        ReplayMode replay = ReplayMode.FromSessionStart)
        => _backing.Subscribe(handler, replay);

    private void OnBackingEvent(InventoryEvent evt)
    {
        var ts = new DateTimeOffset(DateTime.SpecifyKind(evt.Timestamp, DateTimeKind.Utc));
        try
        {
            switch (evt.Kind)
            {
                case InventoryEventKind.Added:
                    _bus.Publish(new Frame<InventoryItemAdded>(
                        ts,
                        new InventoryItemAdded(
                            evt.InstanceId, evt.InternalName, evt.StackSize, evt.SizeConfirmed, evt.Timestamp)));
                    break;
                case InventoryEventKind.Deleted:
                    _bus.Publish(new Frame<InventoryItemRemoved>(
                        ts,
                        new InventoryItemRemoved(
                            evt.InstanceId, evt.InternalName, evt.StackSize, evt.SizeConfirmed, evt.Timestamp)));
                    break;
                case InventoryEventKind.StackChanged:
                    _bus.Publish(new Frame<InventoryStackChanged>(
                        ts,
                        new InventoryStackChanged(
                            evt.InstanceId, evt.InternalName, evt.StackSize, evt.SizeConfirmed, evt.Timestamp)));
                    break;
            }
        }
        catch (Exception ex)
        {
            _diag?.Warn("GameState.Inventory.View", $"View-bus dispatch threw: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shimSubscription.Dispose();
    }
}
