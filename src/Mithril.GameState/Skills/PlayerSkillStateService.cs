using Mithril.GameState.Skills.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Reference;
using Mithril.WorldSim;

namespace Mithril.GameState.Skills;

/// <summary>
/// Player.log skill-state folder + service surface. The single owner of player
/// skill state derived from the log — modules depend on
/// <see cref="IPlayerSkillState"/> rather than re-parsing the stream.
///
/// <para><b>World-simulator migration (issue #618 — Phase 1).</b> Pre-migration
/// this class was a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
/// that owned its own L1-driver subscription. Post-migration it is an
/// <see cref="IFolder{TPayload}"/> registered with <c>IPlayerWorld</c> for the
/// <see cref="SkillFrame"/> payload type: a sibling
/// <see cref="Producers.SkillFrameProducer"/> owns the L1 subscription, parses
/// each line via <see cref="SkillLogParser"/>, and emits one
/// <see cref="SkillsSnapshotFrame"/> per <c>ProcessLoadSkills</c> /
/// <see cref="SkillProgressUpdateFrame"/> per <c>ProcessUpdateSkill</c>. The
/// world drives <see cref="Apply"/> per applied frame in source order; the
/// folder returns <see cref="SkillChange"/> change events which the world
/// publishes on its bus (<c>IPlayerWorld.Bus.Subscribe&lt;SkillChange&gt;</c>)
/// in addition to invoking the legacy
/// <see cref="IPlayerSkillState.SubscribeChanges"/> handlers.</para>
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
/// <para><b>Threading.</b> The world drives <see cref="Apply"/> from its
/// merger thread; folder mutations + legacy handler dispatch run under
/// <see cref="_lock"/>. <see cref="Current"/> reads are lock-free (reference
/// read of an immutable object). <see cref="Subscribe"/> replays and attaches
/// atomically under the same lock the dispatch loop fires under, which closes
/// the late-subscribe race exactly as the pre-migration shape did.</para>
///
/// <para><b>Two consumer channels, one truth.</b>
/// <see cref="SubscribeChanges"/> + <see cref="Subscribe"/> remain the
/// idiomatic surfaces for snapshot / event delivery. The world's bus carries
/// the same <see cref="SkillChange"/> stream for cross-cutting consumers that
/// prefer the architectural <c>IPlayerWorld.Bus.Subscribe&lt;SkillChange&gt;</c>
/// path (design notebook principle 4 — single-world consumers may subscribe
/// directly to the world's bus). Both channels fire on identical events with
/// identical content; back-compat consumers keep working; new code can
/// choose either.</para>
/// </summary>
public sealed class PlayerSkillStateService : IFolder<SkillFrame>, IPlayerSkillState
{
    private readonly IReferenceDataService? _refData;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PlayerSkillSnapshot>> _handlers = new();
    private readonly List<Action<SkillChange>> _changeHandlers = new();
    private volatile PlayerSkillSnapshot _current = PlayerSkillSnapshot.Empty;

    /// <param name="refData">Optional (#470). When present, each projected
    /// <see cref="SkillProgressSnapshot"/> is enriched with authoritative
    /// <see cref="SkillReference"/> metadata (display name, XpTable/umbrella).
    /// Absent (tests, or reference data not yet loaded) → the snapshot keeps
    /// the verified log-only proxies.</param>
    public PlayerSkillStateService(
        IReferenceDataService? refData = null,
        IDiagnosticsSink? diag = null)
    {
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

    /// <summary>
    /// Apply one frame to internal state. The world routes
    /// <see cref="SkillFrame"/> frames here in source-stream order; the folder
    /// mutates state, fires the legacy <see cref="Subscribe"/> /
    /// <see cref="SubscribeChanges"/> handlers, and returns the
    /// <see cref="SkillChange"/> events for the world to surface on its bus.
    /// </summary>
    /// <remarks>
    /// <paramref name="clock"/> is unused — the folder's state derivation is
    /// driven entirely by the frame's own timestamp (which equals
    /// <c>clock.Now</c> at apply-time per the framework). Keeping
    /// frame-timestamp-only ensures the snapshot's
    /// <see cref="PlayerSkillSnapshot.MeasuredAt"/> reads identically before
    /// and after this migration.
    /// </remarks>
    public IReadOnlyList<IChangeEvent> Apply(Frame<SkillFrame> frame, IWorldClock clock)
    {
        _ = clock;
        return frame.Payload switch
        {
            SkillsSnapshotFrame snap => ReplaceAll(snap, frame.Timestamp.UtcDateTime),
            SkillProgressUpdateFrame upd => Upsert(upd, frame.Timestamp.UtcDateTime),
            _ => Array.Empty<IChangeEvent>(),
        };
    }

    /// <summary>Wholesale replace from a <c>ProcessLoadSkills</c> dump. Emits a
    /// <see cref="SkillChangeKind.SnapshotReplace"/> only for skills whose
    /// projection actually differs from (or is new vs.) the prior state — a
    /// no-op re-sync produces no change events.</summary>
    private IReadOnlyList<IChangeEvent> ReplaceAll(SkillsSnapshotFrame snap, DateTime timestamp)
    {
        var map = new Dictionary<string, SkillProgressSnapshot>(snap.Skills.Count, StringComparer.Ordinal);
        foreach (var r in snap.Skills)
        {
            // Last-wins on the (not observed in practice) chance of a dup key —
            // the indexer rather than Add avoids throwing on grammar drift.
            map[r.SkillKey] = Project(r);
        }

        List<IChangeEvent> changes = new();
        lock (_lock)
        {
            var prev = _current.Skills;
            foreach (var (key, cur) in map)
            {
                bool had = prev.TryGetValue(key, out var before);
                if (had && before.Equals(cur)) continue; // unchanged — skip
                changes.Add(new SkillChange(
                    key, had ? before : null, cur, XpGained: 0,
                    SkillChangeKind.SnapshotReplace, timestamp));
            }

            _current = new PlayerSkillSnapshot(map, timestamp, SkillStateSource.LiveLog);
            _diag?.Trace("GameState.Skills",
                $"Skill snapshot replaced: {map.Count} skills @ {timestamp:O}");
            Fire(_current);
            foreach (var c in changes) FireChange((SkillChange)c);
        }
        return changes;
    }

    /// <summary>Single-skill upsert from a <c>ProcessUpdateSkill</c> delta.
    /// Accepted even before the first full snapshot — the resulting partial
    /// state is intentional (see the type docs). Emits one
    /// <see cref="SkillChangeKind.Delta"/> carrying the tick's XP.</summary>
    private IReadOnlyList<IChangeEvent> Upsert(SkillProgressUpdateFrame upd, DateTime timestamp)
    {
        SkillChange change;
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
            _current = new PlayerSkillSnapshot(map, timestamp, SkillStateSource.LiveLog);
            _diag?.Trace("GameState.Skills",
                $"Skill upsert: {key} raw={upd.Skill.Level} +{upd.XpGained}xp @ {timestamp:O}");

            change = new SkillChange(
                key, before, cur, upd.XpGained, SkillChangeKind.Delta, timestamp);

            Fire(_current);
            FireChange(change);
        }
        return new IChangeEvent[] { change };
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
