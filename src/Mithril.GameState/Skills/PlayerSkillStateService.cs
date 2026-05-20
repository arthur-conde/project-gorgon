using Microsoft.Extensions.Hosting;
using Mithril.GameState.Skills.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;

namespace Mithril.GameState.Skills;

/// <summary>
/// Eagerly subscribes to the L1 (#550) driver's LocalPlayer pipe at shell
/// startup and maintains the canonical live <see cref="PlayerSkillSnapshot"/>
/// from <c>ProcessLoadSkills</c> (full replace) and
/// <c>ProcessUpdateSkill</c> (per-skill upsert) lines. The single owner of
/// player skill state derived from the log — modules depend on
/// <see cref="IPlayerSkillState"/> rather than re-parsing the stream.
///
/// <para><b>Self-heal / warm-up.</b> <c>ProcessLoadSkills</c> is emitted at
/// login and again on every zone / session transition, so a wholesale replace
/// on each one keeps state correct even when Mithril starts tailing
/// mid-session. Before the first snapshot of the session,
/// <see cref="Current"/> is <see cref="PlayerSkillSnapshot.Empty"/>; isolated
/// <c>ProcessUpdateSkill</c> lines seen first produce a deliberately partial
/// snapshot (better than nothing — the next <c>ProcessLoadSkills</c> makes it
/// whole). This window is the documented contract.</para>
///
/// <para><b>Threading.</b> The L1 driver delivers envelopes on its pump
/// thread (archetype-A default = <c>DeliveryContext.Inline</c>). The
/// snapshot reference is swapped under <see cref="_lock"/>;
/// <see cref="Current"/> reads are lock-free (reference read of an immutable
/// object). <see cref="Subscribe"/> replays and attaches atomically under
/// the same lock the ingestion loop fires under, which closes the
/// late-subscribe race exactly as <c>InventoryService</c> does.</para>
///
/// <para><b>Containment.</b> The L1 driver wraps each handler invocation
/// in try/catch + rate-limited Warn, retiring the per-service
/// <see cref="ThrottledWarn"/> instance this service used to hold (#550
/// capability C). Failures surface on <c>IDiagnosticsSink</c> under the
/// <c>GameState.Skills</c> category via the driver's
/// <see cref="LogSubscriptionOptions.DiagnosticCategory"/> override. The
/// parser's own <see cref="ThrottledWarn"/> (per-row numeric-overflow
/// guard, #525) is unrelated and stays.</para>
/// </summary>
public sealed class PlayerSkillStateService : BackgroundService, IPlayerSkillState
{
    private readonly ILogStreamDriver _driver;
    private readonly SkillLogParser _parser;
    private readonly IReferenceDataService? _refData;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PlayerSkillSnapshot>> _handlers = new();
    private readonly List<Action<SkillChange>> _changeHandlers = new();
    private volatile PlayerSkillSnapshot _current = PlayerSkillSnapshot.Empty;
    private ILogSubscription? _subscription;

    /// <param name="refData">Optional (#470). When present, each projected
    /// <see cref="SkillProgressSnapshot"/> is enriched with authoritative
    /// <see cref="SkillReference"/> metadata (display name, XpTable/umbrella).
    /// Absent (tests, or reference data not yet loaded) → the snapshot keeps
    /// the verified log-only proxies. Mirrors <c>InventoryService</c>'s
    /// optional <c>IReferenceDataService?</c> dependency.</param>
    public PlayerSkillStateService(
        ILogStreamDriver driver,
        SkillLogParser parser,
        IReferenceDataService? refData = null,
        IDiagnosticsSink? diag = null)
    {
        _driver = driver;
        _parser = parser;
        _refData = refData;
        _diag = diag;
    }

    public PlayerSkillSnapshot Current => _current;

    public IDisposable Subscribe(Action<PlayerSkillSnapshot> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            Invoke(handler, _current);
            _handlers.Add(handler);
            return new Unsub(this, _handlers, handler);
        }
    }

    public IDisposable SubscribeChanges(Action<SkillChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            // No replay — a SkillChange is an event, not state. Current state
            // is read via Current.
            _changeHandlers.Add(handler);
            return new Unsub(this, _changeHandlers, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Skills", "Subscribing to L1 driver (LocalPlayer pipe) for skill-state events");
        // archetype-A defaults — FromSessionStart replay + Inline delivery.
        // The parser consumes the envelope-stripped LocalPlayerLogLine.Data
        // directly — L0.5 (#532) owns actor classification, downstream
        // never re-matches the envelope (#550 PR #555 review).
        _subscription = _driver.Subscribe<LocalPlayerLogLine>(
            envelope =>
            {
                var ts = envelope.Payload.Timestamp.UtcDateTime;
                switch (_parser.TryParse(envelope.Payload.Data, ts))
                {
                    case SkillsSnapshotEvent snap:
                        ReplaceAll(snap);
                        break;
                    case SkillProgressUpdateEvent upd:
                        Upsert(upd);
                        break;
                }
                return ValueTask.CompletedTask;
            },
            new LogSubscriptionOptions
            {
                ReplayMode = ReplayMode.FromSessionStart,
                DeliveryContext = DeliveryContext.Inline,
                DiagnosticCategory = "GameState.Skills",
            });

        try { await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected on host stop */ }
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

    /// <summary>Wholesale replace from a <c>ProcessLoadSkills</c> dump. Emits a
    /// <see cref="SkillChangeKind.SnapshotReplace"/> only for skills whose
    /// projection actually differs from (or is new vs.) the prior state — a
    /// no-op re-sync produces no change events.</summary>
    private void ReplaceAll(SkillsSnapshotEvent snap)
    {
        var map = new Dictionary<string, SkillProgressSnapshot>(snap.Skills.Count, StringComparer.Ordinal);
        foreach (var r in snap.Skills)
        {
            // Last-wins on the (not observed in practice) chance of a dup key —
            // the indexer rather than Add avoids throwing on grammar drift.
            map[r.SkillKey] = Project(r);
        }

        lock (_lock)
        {
            var prev = _current.Skills;
            List<SkillChange>? changes = _changeHandlers.Count == 0 ? null : new();
            if (changes is not null)
            {
                foreach (var (key, cur) in map)
                {
                    bool had = prev.TryGetValue(key, out var before);
                    if (had && before.Equals(cur)) continue; // unchanged — skip
                    changes.Add(new SkillChange(
                        key, had ? before : null, cur, XpGained: 0,
                        SkillChangeKind.SnapshotReplace, snap.Timestamp));
                }
            }

            _current = new PlayerSkillSnapshot(map, snap.Timestamp, SkillStateSource.LiveLog);
            _diag?.Trace("GameState.Skills",
                $"Skill snapshot replaced: {map.Count} skills @ {snap.Timestamp:O}");
            Fire(_current);
            if (changes is not null)
                foreach (var c in changes) FireChange(c);
        }
    }

    /// <summary>Single-skill upsert from a <c>ProcessUpdateSkill</c> delta.
    /// Accepted even before the first full snapshot — the resulting partial
    /// state is intentional (see the type docs). Emits one
    /// <see cref="SkillChangeKind.Delta"/> carrying the tick's XP.</summary>
    private void Upsert(SkillProgressUpdateEvent upd)
    {
        lock (_lock)
        {
            var key = upd.Skill.SkillKey;
            SkillProgressSnapshot? before =
                _current.Skills.TryGetValue(key, out var b) ? b : null;
            var cur = Project(upd.Skill);

            // Copy-on-write so any consumer holding the prior snapshot keeps a
            // stable, immutable view.
            var map = new Dictionary<string, SkillProgressSnapshot>(_current.Skills, StringComparer.Ordinal)
            {
                [key] = cur,
            };
            _current = new PlayerSkillSnapshot(map, upd.Timestamp, SkillStateSource.LiveLog);
            _diag?.Trace("GameState.Skills",
                $"Skill upsert: {key} raw={upd.Skill.Level} +{upd.XpGained}xp @ {upd.Timestamp:O}");
            Fire(_current);
            FireChange(new SkillChange(
                key, before, cur, upd.XpGained, SkillChangeKind.Delta, upd.Timestamp));
        }
    }

    private SkillProgressSnapshot Project(SkillProgressRecord r) => new(
        Level: r.Level,
        BonusLevels: r.BonusLevels,
        XpTowardNextLevel: r.XpTowardNextLevel,
        XpNeededForNextLevel: r.XpNeededForNextLevel,
        MaxLevel: r.MaxLevel,
        Reference: ResolveReference(r.SkillKey));

    /// <summary>Authoritative metadata from reference data, or <c>null</c> when
    /// reference data is absent or the skill isn't in the catalog (then the
    /// snapshot falls back to the verified log-only proxies).</summary>
    private SkillReference? ResolveReference(string skillKey)
        => _refData is not null && _refData.Skills.TryGetValue(skillKey, out var e)
            ? new SkillReference(e.DisplayName, e.XpTable, e.MaxBonusLevels)
            : null;

    /// <summary>MUST be called with <see cref="_lock"/> held.</summary>
    private void Fire(PlayerSkillSnapshot snapshot)
    {
        foreach (var h in _handlers) Invoke(h, snapshot);
    }

    /// <summary>MUST be called with <see cref="_lock"/> held.</summary>
    private void FireChange(SkillChange change)
    {
        foreach (var h in _changeHandlers) InvokeChange(h, change);
    }

    private void Invoke(Action<PlayerSkillSnapshot> handler, PlayerSkillSnapshot snapshot)
    {
        try { handler(snapshot); }
        catch (Exception ex) { _diag?.Warn("GameState.Skills", $"Subscriber threw: {ex.Message}"); }
    }

    private void InvokeChange(Action<SkillChange> handler, SkillChange change)
    {
        try { handler(change); }
        catch (Exception ex) { _diag?.Warn("GameState.Skills", $"Change subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Removes <paramref name="handler"/> from the list it was added to, under
    /// the owner's lock. One implementation serves both the snapshot and the
    /// change channels.
    /// </summary>
    private sealed class Unsub : IDisposable
    {
        private PlayerSkillStateService? _owner;
        private readonly System.Collections.IList _list;
        private readonly object _handler;

        public Unsub(PlayerSkillStateService owner, System.Collections.IList list, object handler)
        {
            _owner = owner;
            _list = list;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock) { _list.Remove(_handler); }
        }
    }
}
