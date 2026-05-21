using System.Text.RegularExpressions;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Microsoft.Extensions.Hosting;

namespace Mithril.GameState.Effects;

/// <summary>
/// Hosted-service implementation of <see cref="IPlayerEffectsStateService"/>.
/// Subscribes to the L1 driver's <see cref="LocalPlayerLogLine"/> pipe with
/// <see cref="ReplayMode.FromSessionStart"/> — the same shape
/// <see cref="Mithril.GameState.Inventory.InventoryService"/> uses (#590 spec,
/// Behaviour section). Maintains the live catalog-id-keyed map plus an
/// internal event log used to atomically replay history to late subscribers
/// (post-#585 React-channel contract).
///
/// <para><b>Threading.</b> A single <c>_lock</c> guards <c>_active</c>,
/// <c>_eventLog</c>, <c>_handlers</c>, and <c>_unnamed</c>. Both the L1
/// pump (via <c>OnLocalPlayer</c>) and <see cref="Subscribe"/> callers take
/// it; live dispatch happens under the lock so the
/// Subscribe-vs-live-event race is impossible (the new subscriber either
/// ran its replay before the next event fires, or attached after — never
/// in between).</para>
///
/// <para><b>Why a self-feeding BackgroundService.</b> Mirrors
/// <see cref="Mithril.GameState.Celestial.PlayerCelestialStateService"/>:
/// live game-state shared across multiple downstream consumers (Pippin
/// Gourmand, Vampirism sun-damage, Saruman Words-of-Power — see issue #590
/// "Consumers") that must populate at shell startup independent of any
/// module activation gate.</para>
/// </summary>
public sealed partial class PlayerEffectsStateService : BackgroundService, IPlayerEffectsStateService
{
    // ProcessAddEffects(targetCharId, sourceCharId, "[<catalogId1>, <catalogId2>, ...]", <bool>)
    // The bracketed list arrives as a quoted string; we capture the inner content
    // (without the outer quotes) and the bool flag separately. The list tolerates
    // trailing spaces / commas (e.g. "[15361, ]") per captured samples.
    [GeneratedRegex(@"ProcessAddEffects\((-?\d+),\s*(-?\d+),\s*""\[([^\]]*)\]"",\s*(True|False)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex AddEffectsRx();

    // ProcessRemoveEffects(targetCharId, [<instanceId1>, <instanceId2>, ...])
    // The bracketed list is UNQUOTED (not a string literal).
    [GeneratedRegex(@"ProcessRemoveEffects\((-?\d+),\s*\[([^\]]*)\]\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex RemoveEffectsRx();

    // ProcessUpdateEffectName(targetCharId, effectInstanceId, "<displayName>")
    [GeneratedRegex(@"ProcessUpdateEffectName\((-?\d+),\s*(-?\d+),\s*""((?:[^""\\]|\\.)*)""\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex UpdateEffectNameRx();

    /// <summary>
    /// Soft cap on the internal event-log size. PG sessions emit ~250 effect
    /// verbs per the wiki captures, so 10,000 is generous (a couple of orders
    /// of magnitude above expected). One-time <see cref="DiagnosticLevel.Warn"/>
    /// if exceeded — the cap protects the log from unbounded growth on a
    /// degenerate stream, not from normal use.
    /// </summary>
    internal const int EventLogSoftCap = 10_000;

    private readonly ILogStreamDriver _driver;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly Dictionary<int, EffectState> _active = new();
    private readonly List<EffectEvent> _eventLog = new();
    private readonly List<(Action<EffectEvent> Handler, ReplayMode Replay)> _handlers = new();

    // Stack of un-named catalog ids (most-recent-first) used to correlate a
    // ProcessUpdateEffectName back to its preceding ProcessAddEffects entry.
    // Spec wording: "the entry that was most recently added and still lacks
    // an InstanceId" — LIFO/stack. Captured Add/Update interleaving is 1:1
    // ([302] Add → Update 259328 → [303] Add → Update 259329), so the stack
    // depth is typically 0 or 1; the data structure is defensive against a
    // batched-Add pattern PG might emit but we haven't captured.
    private readonly Stack<int> _unnamed = new();

    private ILogSubscription? _subscription;
    private bool _eventLogCapWarned;

    public PlayerEffectsStateService(ILogStreamDriver driver, IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _diag = diag;
    }

    public bool TryGet(int catalogId, out EffectState state)
    {
        lock (_lock)
        {
            return _active.TryGetValue(catalogId, out state);
        }
    }

    public IReadOnlyDictionary<int, EffectState> ActiveEffects
    {
        get
        {
            lock (_lock)
            {
                // Defensive copy — callers iterate without holding the service lock.
                return new Dictionary<int, EffectState>(_active);
            }
        }
    }

    public IDisposable Subscribe(
        Action<EffectEvent> handler,
        ReplayMode replay = ReplayMode.FromSessionStart)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (replay == ReplayMode.FromSessionStart)
            {
                // Atomic replay under the lock — same shape as InventoryService.Subscribe.
                // The Fire path also holds _lock, so a new subscriber either ran its
                // replay strictly before the next live event or attached strictly after.
                foreach (var evt in _eventLog)
                {
                    Invoke(handler, evt);
                }
            }
            _handlers.Add((handler, replay));
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Effects",
            "Subscribing to L1 driver (LocalPlayer pipe) for ProcessAddEffects / "
            + "ProcessRemoveEffects / ProcessUpdateEffectName");
        try
        {
            _subscription = _driver.Subscribe<LocalPlayerLogLine>(
                OnLocalPlayer,
                new LogSubscriptionOptions
                {
                    ReplayMode = ReplayMode.FromSessionStart,
                    DeliveryContext = DeliveryContext.Inline,
                    DiagnosticCategory = "GameState.Effects",
                });

            try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on host stop */ }
        }
        finally
        {
            _subscription?.Dispose();
            _subscription = null;
        }
    }

    public override void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
        base.Dispose();
    }

    private ValueTask OnLocalPlayer(LogEnvelope<LocalPlayerLogLine> envelope)
    {
        var data = envelope.Payload.Data;
        var ts = ToOffset(envelope.Payload.Timestamp.UtcDateTime);

        var add = AddEffectsRx().Match(data);
        if (add.Success
            && long.TryParse(add.Groups[2].ValueSpan, out var sourceCharId))
        {
            var listBody = add.Groups[3].Value;
            var isLive = add.Groups[4].Value == "True";
            HandleAddEffects(sourceCharId, listBody, isLive, ts);
            return ValueTask.CompletedTask;
        }

        var rem = RemoveEffectsRx().Match(data);
        if (rem.Success)
        {
            HandleRemoveEffects(rem.Groups[2].Value, ts);
            return ValueTask.CompletedTask;
        }

        var upd = UpdateEffectNameRx().Match(data);
        if (upd.Success
            && long.TryParse(upd.Groups[2].ValueSpan, out var instanceId))
        {
            HandleUpdateEffectName(instanceId, upd.Groups[3].Value, ts);
        }
        return ValueTask.CompletedTask;
    }

    private void HandleAddEffects(long sourceCharId, string listBody, bool isLive, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            foreach (var token in EnumerateIntTokens(listBody))
            {
                if (!int.TryParse(token, out var catalogId)) continue;

                if (_active.TryGetValue(catalogId, out var existing))
                {
                    // Idempotent re-apply / re-emit. Per #590 spec: refresh AppliedAt,
                    // no Added event fires. Mirrors InventoryService's add-reemit at
                    // InventoryService.cs:320-326. Applies to both True (passive
                    // equipment-bonus re-apply at zone-load) and False (login snapshot).
                    _active[catalogId] = existing with { AppliedAt = timestamp };
                    _diag?.Trace("GameState.Effects",
                        $"Add-reemit catalog={catalogId} source={sourceCharId} live={isLive}");
                    continue;
                }

                var state = new EffectState(
                    CatalogId: catalogId,
                    InstanceId: null,
                    DisplayName: null,
                    SourceCharId: sourceCharId,
                    AppliedAt: timestamp);
                _active[catalogId] = state;
                _unnamed.Push(catalogId);

                var evt = new EffectEvent(EffectEventKind.Added, state, timestamp);
                AppendEventLog(evt);
                _diag?.Trace("GameState.Effects",
                    $"Add    catalog={catalogId} source={sourceCharId} live={isLive} (total={_active.Count})");
                Fire(evt);
            }
        }
    }

    private void HandleRemoveEffects(string listBody, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            foreach (var token in EnumerateIntTokens(listBody))
            {
                if (!long.TryParse(token, out var instanceId)) continue;

                // Linear scan over _active — bounded by the live set size,
                // typically << 100. Spec accepts catalog-id-only entries can
                // never be removed by id (no InstanceId bridge); only entries
                // that received a ProcessUpdateEffectName are removable here.
                int? matchedCatalogId = null;
                foreach (var (catalogId, state) in _active)
                {
                    if (state.InstanceId == instanceId)
                    {
                        matchedCatalogId = catalogId;
                        break;
                    }
                }

                if (matchedCatalogId is null)
                {
                    _diag?.Warn("GameState.Effects",
                        $"Remove instance={instanceId} — no entry with that InstanceId in live set, ignored");
                    continue;
                }

                var removed = _active[matchedCatalogId.Value];
                _active.Remove(matchedCatalogId.Value);
                var evt = new EffectEvent(EffectEventKind.Removed, removed, timestamp);
                AppendEventLog(evt);
                _diag?.Trace("GameState.Effects",
                    $"Remove catalog={matchedCatalogId.Value} instance={instanceId} (total={_active.Count})");
                Fire(evt);
            }
        }
    }

    private void HandleUpdateEffectName(long instanceId, string displayName, DateTimeOffset timestamp)
    {
        lock (_lock)
        {
            // Pop the most recently added un-named entry. PG's captured pattern
            // interleaves Add/Update 1:1 at the same instant ([302] Add →
            // Update 259328 → [303] Add → Update 259329), so the stack depth is
            // typically 1 and pop semantics is unambiguous. Spec accepts a
            // missing pair as "best-effort, drop with Trace; no synthetic Add."
            while (_unnamed.Count > 0)
            {
                var catalogId = _unnamed.Pop();
                if (!_active.TryGetValue(catalogId, out var state))
                {
                    // The un-named entry was removed before its Update arrived.
                    // Keep popping — the next-most-recent un-named is the candidate.
                    continue;
                }
                if (state.InstanceId is not null)
                {
                    // Already named (defensive — pop shouldn't surface a named entry,
                    // but the stack and _active are not strictly in lock-step if a
                    // double-Update lands for the same catalog id). Skip.
                    continue;
                }

                var updated = state with { InstanceId = instanceId, DisplayName = displayName };
                _active[catalogId] = updated;
                var evt = new EffectEvent(EffectEventKind.DisplayNameChanged, updated, timestamp);
                AppendEventLog(evt);
                _diag?.Trace("GameState.Effects",
                    $"Update catalog={catalogId} instance={instanceId} name=\"{displayName}\"");
                Fire(evt);
                return;
            }

            // No un-named candidate. Could happen if the Update fires for an
            // effect whose Add we missed (mid-session attach without snapshot
            // replay), or a double-Update for an already-named entry.
            _diag?.Warn("GameState.Effects",
                $"Update instance={instanceId} name=\"{displayName}\" — no un-named candidate, dropped");
        }
    }

    /// <summary>
    /// Tokenize a comma-separated list body (e.g. <c>"302, 303, "</c> or
    /// <c>"259278,"</c>). Tolerates trailing empty tokens — PG's captured
    /// shape for both Add (<c>"[15361, ]"</c>) and Remove (<c>"[259278,]"</c>)
    /// commonly ends with a trailing comma.
    /// </summary>
    private static IEnumerable<string> EnumerateIntTokens(string listBody)
    {
        if (string.IsNullOrEmpty(listBody)) yield break;
        foreach (var raw in listBody.Split(','))
        {
            var token = raw.Trim();
            if (token.Length == 0) continue;
            yield return token;
        }
    }

    /// <summary>
    /// MUST be called with <see cref="_lock"/> held. Appends an event to the
    /// internal log used by <see cref="Subscribe"/>'s replay path. Soft cap
    /// (<see cref="EventLogSoftCap"/>) protects against unbounded growth on
    /// a degenerate stream; a one-time <see cref="DiagnosticLevel.Warn"/>
    /// fires when the cap is first exceeded. Past the cap the log keeps
    /// growing — dropping events would silently break the replay contract.
    /// </summary>
    private void AppendEventLog(EffectEvent evt)
    {
        _eventLog.Add(evt);
        if (_eventLog.Count > EventLogSoftCap && !_eventLogCapWarned)
        {
            _eventLogCapWarned = true;
            _diag?.Warn("GameState.Effects",
                $"Event-log size exceeded soft cap ({EventLogSoftCap}). "
                + "Replay-on-Subscribe will continue to deliver every event in order; "
                + "log keeps growing (no drop). Investigate if not expected.");
        }
    }

    /// <summary>
    /// MUST be called with <see cref="_lock"/> held. Dispatches to every
    /// currently-attached handler (replay handlers receive live events;
    /// live-only handlers also do). Holding the lock across dispatch is the
    /// same atomicity guarantee <see cref="Mithril.GameState.Inventory.InventoryService.Subscribe"/>
    /// makes: a new subscriber either ran its replay before this Fire and
    /// will receive the live event, or runs after and saw the event in its
    /// replay.
    /// </summary>
    private void Fire(EffectEvent evt)
    {
        foreach (var (handler, _) in _handlers) Invoke(handler, evt);
    }

    private void Invoke(Action<EffectEvent> handler, EffectEvent evt)
    {
        try { handler(evt); }
        catch (Exception ex) { _diag?.Warn("GameState.Effects", $"Subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Player.log timestamps are normalized upstream by the L0 source clock
    /// (UTC <see cref="DateTimeOffset"/>); we receive <c>.UtcDateTime</c>
    /// here. Stamp the kind defensively so the lifted
    /// <see cref="DateTimeOffset"/> always has offset +00:00 rather than the
    /// host's local offset.
    /// </summary>
    private static DateTimeOffset ToOffset(DateTime ts) =>
        new(DateTime.SpecifyKind(ts, DateTimeKind.Utc));

    private sealed class Subscription : IDisposable
    {
        private PlayerEffectsStateService? _owner;
        private readonly Action<EffectEvent> _handler;

        public Subscription(PlayerEffectsStateService owner, Action<EffectEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock)
            {
                for (int i = owner._handlers.Count - 1; i >= 0; i--)
                {
                    if (owner._handlers[i].Handler == _handler)
                    {
                        owner._handlers.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }
}
