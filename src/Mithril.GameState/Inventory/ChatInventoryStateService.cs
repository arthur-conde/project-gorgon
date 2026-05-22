using Mithril.Shared.Diagnostics;
using Mithril.WorldSim;

namespace Mithril.GameState.Inventory;

/// <summary>
/// Chat-side inventory-state folder + service surface (#602). The ChatWorld
/// half of the post-split inventory: name-keyed time-series of stack-size
/// observations folded from the chat <c>[Status]</c> channel. Emits one
/// <see cref="ChatInventoryObserved"/> change event per applied frame on the
/// ChatWorld bus for downstream view-layer composition.
///
/// <para><b>World-simulator role.</b> Registered with <c>IChatWorld</c> as an
/// <see cref="IFolder{ChatInventoryObservationFrame}"/> by the GameState DI
/// extension. A sibling
/// <see cref="Producers.ChatInventoryFrameProducer"/> parses
/// <see cref="Mithril.Shared.Logging.RawLogLine"/> envelopes from the chat
/// stream into <see cref="ChatInventoryObservationFrame"/> instances.</para>
///
/// <para><b>Last-observation map.</b> <see cref="TryGetLastObservation"/>
/// returns only the most-recent observation per display name. The full
/// time-series is not retained — view-layer pairing reads the most recent
/// observation within the correlator's TTL window from the change-event
/// stream, not from this folder's state. Keeping a name-keyed last-observation
/// dictionary handles the rare "TryGet from a non-bus consumer" path without
/// growing memory linearly with session length.</para>
///
/// <para><b>Threading.</b> The world drives <see cref="Apply"/> from its
/// merger thread; folder mutations run under <see cref="_lock"/>.</para>
/// </summary>
public sealed class ChatInventoryStateService : IFolder<ChatInventoryObservationFrame>, IChatInventoryState
{
    private readonly IDiagnosticsSink? _diag;
    private readonly object _lock = new();
    private readonly Dictionary<string, (int Count, DateTime Timestamp)> _last = new(StringComparer.Ordinal);

    public ChatInventoryStateService(IDiagnosticsSink? diag = null)
    {
        _diag = diag;
    }

    public bool TryGetLastObservation(string displayName, out int count, out DateTime timestamp)
    {
        lock (_lock)
        {
            if (_last.TryGetValue(displayName, out var entry))
            {
                count = entry.Count;
                timestamp = entry.Timestamp;
                return true;
            }
        }
        count = 0;
        timestamp = default;
        return false;
    }

    public IReadOnlyList<IChangeEvent> Apply(Frame<ChatInventoryObservationFrame> frame, IWorldClock clock)
    {
        _ = clock;
        var ts = frame.Timestamp.UtcDateTime;
        lock (_lock)
        {
            _last[frame.Payload.DisplayName] = (frame.Payload.Count, ts);
        }
        _diag?.Trace("GameState.Inventory.Chat",
            $"Observed name='{frame.Payload.DisplayName}' count={frame.Payload.Count} @ {ts:O}");
        return new IChangeEvent[]
        {
            new ChatInventoryObserved(frame.Payload.DisplayName, frame.Payload.Count, ts),
        };
    }
}
