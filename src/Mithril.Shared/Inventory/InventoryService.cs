using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.Shared.Inventory;

/// <summary>
/// Eagerly subscribes to <see cref="IPlayerLogStream"/> at shell startup and
/// maintains the canonical <c>instanceId → InternalName</c> map. The stream's
/// session-replay buffer guarantees that the initial flush of
/// <c>ProcessAddItem</c> events is observed here regardless of subscriber
/// ordering; modules that need inventory lookups should depend on
/// <see cref="IInventoryService"/> rather than re-parsing the log.
///
/// Subscribers attach via <see cref="Subscribe"/>, which atomically replays
/// the current live-map contents before going live. This closes the late-join
/// race that a plain event would otherwise leave open: if InventoryService has
/// already processed session-replay lines before the subscriber attaches, the
/// subscriber would otherwise miss those <c>Added</c> events permanently.
/// </summary>
public sealed partial class InventoryService : BackgroundService, IInventoryService
{
    // ProcessAddItem(InternalName(instanceId), slot, bool)
    [GeneratedRegex(@"ProcessAddItem\((\w+)\((\d+)\),", RegexOptions.CultureInvariant)]
    private static partial Regex AddItemRx();

    // ProcessDeleteItem(instanceId)
    [GeneratedRegex(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeleteItemRx();

    private readonly IPlayerLogStream _stream;
    private readonly IDiagnosticsSink? _diag;

    // _subLock guards both _map and _handlers. The ingestion loop mutates the
    // map and dispatches to handlers under the same lock so a concurrent
    // Subscribe call sees a consistent snapshot — every entry is either
    // replayed to the new handler OR delivered as a live event after Subscribe
    // returns, never both, never neither.
    private readonly object _subLock = new();
    private readonly Dictionary<long, MapEntry> _map = new();
    private readonly List<Action<InventoryEvent>> _handlers = new();

    private readonly record struct MapEntry(string InternalName, DateTime Timestamp, bool Deleted);

    public InventoryService(IPlayerLogStream stream, IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _diag = diag;
    }

    public bool TryResolve(long instanceId, out string internalName)
    {
        lock (_subLock)
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

    public IDisposable Subscribe(Action<InventoryEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_subLock)
        {
            // Replay current live map state to this handler only. Skip
            // entries marked Deleted — they're retained for TryResolve but
            // shouldn't surface as Added events to a fresh subscriber.
            foreach (var (id, entry) in _map)
            {
                if (entry.Deleted) continue;
                Invoke(handler, new InventoryEvent(
                    InventoryEventKind.Added, id, entry.InternalName, entry.Timestamp));
            }
            _handlers.Add(handler);
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("Inventory", "Subscribing to Player.log for inventory events");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            var add = AddItemRx().Match(raw.Line);
            if (add.Success && long.TryParse(add.Groups[2].ValueSpan, out var addId))
            {
                var name = add.Groups[1].Value;
                lock (_subLock)
                {
                    _map[addId] = new MapEntry(name, raw.Timestamp, Deleted: false);
                    _diag?.Trace("Inventory", $"Add    id={addId} name={name} (total={_map.Count})");
                    Fire(new InventoryEvent(InventoryEventKind.Added, addId, name, raw.Timestamp));
                }
                continue;
            }

            var del = DeleteItemRx().Match(raw.Line);
            if (del.Success && long.TryParse(del.Groups[1].ValueSpan, out var delId))
            {
                lock (_subLock)
                {
                    if (!_map.TryGetValue(delId, out var entry))
                    {
                        _diag?.Trace("Inventory", $"Delete id={delId} — not in map, ignored");
                        continue;
                    }
                    if (entry.Deleted)
                    {
                        // Already marked deleted; suppress the duplicate event.
                        continue;
                    }
                    // Mark as deleted but retain the entry so concurrent TryResolve
                    // callers (e.g. Arwen's FavorIngestionService) can still resolve
                    // an id whose delete line they've already read past.
                    _map[delId] = entry with { Deleted = true, Timestamp = raw.Timestamp };
                    _diag?.Trace("Inventory", $"Delete id={delId} name={entry.InternalName} (retained)");
                    Fire(new InventoryEvent(InventoryEventKind.Deleted, delId, entry.InternalName, raw.Timestamp));
                }
            }
        }
    }

    /// <summary>
    /// MUST be called with <see cref="_subLock"/> held. Dispatches an event to
    /// every currently-attached handler. Holding the lock during dispatch is
    /// what makes the Subscribe-vs-live-event race impossible: a new
    /// subscriber either ran its replay before this Fire (and will receive
    /// the live event) or runs after (and saw the entry in its replay).
    /// </summary>
    private void Fire(InventoryEvent evt)
    {
        foreach (var h in _handlers) Invoke(h, evt);
    }

    private void Invoke(Action<InventoryEvent> handler, InventoryEvent evt)
    {
        try { handler(evt); }
        catch (Exception ex) { _diag?.Warn("Inventory", $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class Subscription : IDisposable
    {
        private InventoryService? _owner;
        private readonly Action<InventoryEvent> _handler;

        public Subscription(InventoryService owner, Action<InventoryEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._subLock) { owner._handlers.Remove(_handler); }
        }
    }
}
