using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<long, string> _map = new();

    public InventoryService(IPlayerLogStream stream, IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _diag = diag;
    }

    public event EventHandler<InventoryItem>? ItemAdded;
    public event EventHandler<InventoryItem>? ItemDeleted;

    public bool TryResolve(long instanceId, out string internalName)
    {
        if (_map.TryGetValue(instanceId, out var name))
        {
            internalName = name;
            return true;
        }
        internalName = "";
        return false;
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
                _map[addId] = name;
                _diag?.Trace("Inventory", $"Add    id={addId} name={name} (total={_map.Count})");
                try { ItemAdded?.Invoke(this, new InventoryItem(addId, name)); }
                catch (Exception ex) { _diag?.Warn("Inventory", $"ItemAdded handler threw: {ex.Message}"); }
                continue;
            }

            var del = DeleteItemRx().Match(raw.Line);
            if (del.Success && long.TryParse(del.Groups[1].ValueSpan, out var delId))
            {
                if (!_map.TryGetValue(delId, out var name))
                {
                    _diag?.Trace("Inventory", $"Delete id={delId} — not in map, ignored");
                    continue;
                }
                // Intentionally retain the map entry. Concurrent subscribers (e.g.
                // Arwen's FavorIngestionService) call TryResolve on their own pace;
                // removing here would race with their log-stream read order.
                _diag?.Trace("Inventory", $"Delete id={delId} name={name} (retained)");
                try { ItemDeleted?.Invoke(this, new InventoryItem(delId, name)); }
                catch (Exception ex) { _diag?.Warn("Inventory", $"ItemDeleted handler threw: {ex.Message}"); }
            }
        }
    }
}
