using Mithril.Shared.Diagnostics;
using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Player.log inventory-state folder + service surface (#602). The PlayerWorld
/// half of the post-split inventory: instance-id-keyed ledger of
/// <c>InternalName</c>s, no stack-size column. Emits
/// <see cref="PlayerInventoryAdded"/>, <see cref="PlayerInventoryRemoved"/>,
/// and <see cref="PlayerInventoryStackUpdated"/> change events on the
/// PlayerWorld bus for downstream view-layer composition.
///
/// <para><b>World-simulator role.</b> Registered with <c>IPlayerWorld</c> as
/// an <see cref="IFolder{PlayerInventoryFrame}"/> by the GameState DI
/// extension. A sibling <see cref="Producers.PlayerInventoryFrameProducer"/>
/// owns the L1 LocalPlayer subscription, parses each line into a
/// <see cref="PlayerInventoryFrame"/> subtype, and feeds those into the
/// world's merger. The world routes frames to <see cref="Apply"/> in
/// source-stream order; the folder mutates the ledger and returns change
/// events for the world to publish on its bus.</para>
///
/// <para><b>Retained-on-delete invariant.</b> <c>ProcessDeleteItem</c> marks
/// the entry deleted but keeps it in the map so concurrent
/// <see cref="TryResolve"/> callers — chiefly Arwen's gift-attribution flow,
/// which queries the inventory ledger to resolve an id whose delete line was
/// the trigger — still observe the <c>InternalName</c>. This mirrors the
/// pre-split <c>IInventoryService.TryResolve</c> behaviour exactly.</para>
///
/// <para><b>Threading.</b> The world drives <see cref="Apply"/> from its
/// merger thread; folder mutations run under <see cref="_lock"/>.
/// <see cref="TryResolve"/> reads under the same lock — short critical
/// section, no allocation.</para>
/// </summary>
public sealed class PlayerInventoryStateService : IFolder<PlayerInventoryFrame>, IPlayerInventoryState
{
    private readonly IDiagnosticsSink? _diag;
    private readonly object _lock = new();
    private readonly Dictionary<long, MapEntry> _map = new();

    private readonly record struct MapEntry(string InternalName, DateTime Timestamp, bool Deleted);

    public PlayerInventoryStateService(IDiagnosticsSink? diag = null)
    {
        _diag = diag;
    }

    public bool TryResolve(long instanceId, out string internalName)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(instanceId, out var entry))
            {
                internalName = entry.InternalName;
                return true;
            }
        }
        internalName = "";
        return false;
    }

    public IReadOnlyList<IChangeEvent> Apply(Frame<PlayerInventoryFrame> frame, IWorldClock clock)
    {
        _ = clock;
        var ts = frame.Timestamp.UtcDateTime;
        return frame.Payload switch
        {
            PlayerInventoryAddFrame add => HandleAdd(add.InstanceId, add.InternalName, ts),
            PlayerInventoryRemoveFrame rm => HandleRemove(rm.InstanceId, ts),
            PlayerInventoryUpdateItemCodeFrame upd => HandleUpdateCode(upd.InstanceId, upd.Code, ts),
            PlayerInventoryVaultWithdrawFrame vw => HandleVaultWithdraw(vw.InstanceId, vw.StackSize, ts),
            _ => Array.Empty<IChangeEvent>(),
        };
    }

    private IReadOnlyList<IChangeEvent> HandleAdd(long instanceId, string internalName, DateTime timestamp)
    {
        lock (_lock)
        {
            // Re-emission pulse (zone change / server resync) for an already-tracked
            // InstanceId. Update the timestamp but don't re-fire — Player.log emits
            // these on every login + zone transition. Mirrors the pre-split
            // InventoryService's "Add-reemit" diagnostic path.
            if (_map.TryGetValue(instanceId, out var existing) && !existing.Deleted)
            {
                _map[instanceId] = existing with { Timestamp = timestamp };
                _diag?.Trace("GameState.Inventory.Player",
                    $"Add-reemit id={instanceId} name={existing.InternalName}");
                return Array.Empty<IChangeEvent>();
            }

            _map[instanceId] = new MapEntry(internalName, timestamp, Deleted: false);
            _diag?.Trace("GameState.Inventory.Player",
                $"Add    id={instanceId} name={internalName} (total={_map.Count})");
            return new IChangeEvent[]
            {
                new PlayerInventoryAdded(instanceId, internalName, timestamp),
            };
        }
    }

    private IReadOnlyList<IChangeEvent> HandleRemove(long instanceId, DateTime timestamp)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(instanceId, out var entry))
            {
                _diag?.Trace("GameState.Inventory.Player",
                    $"Remove id={instanceId} — not in map, ignored");
                return Array.Empty<IChangeEvent>();
            }
            if (entry.Deleted)
            {
                // Already marked deleted; suppress duplicate.
                return Array.Empty<IChangeEvent>();
            }
            _map[instanceId] = entry with { Deleted = true, Timestamp = timestamp };
            _diag?.Trace("GameState.Inventory.Player",
                $"Remove id={instanceId} name={entry.InternalName} (retained)");
            return new IChangeEvent[]
            {
                new PlayerInventoryRemoved(instanceId, entry.InternalName, timestamp),
            };
        }
    }

    private IReadOnlyList<IChangeEvent> HandleUpdateCode(long instanceId, long code, DateTime timestamp)
    {
        // Decode: high 16 bits + 1 = post-event stack size; low 16 bits = TypeID (unused here).
        var newSize = (int)(code >> 16) + 1;
        if (newSize <= 0) return Array.Empty<IChangeEvent>();

        lock (_lock)
        {
            if (!_map.TryGetValue(instanceId, out var entry))
            {
                // Update for an InstanceId we've never seen — PG can emit these for
                // entries that pre-date the session log. Skip; we have no
                // InternalName to attribute the update to.
                _diag?.Trace("GameState.Inventory.Player",
                    $"UpdateCode id={instanceId} size={newSize} — not in map, ignored");
                return Array.Empty<IChangeEvent>();
            }
            _diag?.Trace("GameState.Inventory.Player",
                $"UpdateCode id={instanceId} name={entry.InternalName} size={newSize}");
            return new IChangeEvent[]
            {
                new PlayerInventoryStackUpdated(instanceId, entry.InternalName, newSize, timestamp),
            };
        }
    }

    private IReadOnlyList<IChangeEvent> HandleVaultWithdraw(long instanceId, int stackSize, DateTime timestamp)
    {
        if (stackSize <= 0) return Array.Empty<IChangeEvent>();
        lock (_lock)
        {
            // RemoveFromStorageVault pairs with the AddItem of a vault withdrawal landing
            // in an empty bag (the bag-side InstanceId). Only update if we know this id —
            // that filters out vault-side ids we don't track.
            if (!_map.TryGetValue(instanceId, out var entry)) return Array.Empty<IChangeEvent>();
            _diag?.Trace("GameState.Inventory.Player",
                $"VaultWithdraw id={instanceId} name={entry.InternalName} size={stackSize}");
            return new IChangeEvent[]
            {
                new PlayerInventoryStackUpdated(instanceId, entry.InternalName, stackSize, timestamp),
            };
        }
    }
}
