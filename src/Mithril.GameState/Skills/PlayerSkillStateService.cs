using Microsoft.Extensions.Hosting;
using Mithril.GameState.Skills.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Skills;

/// <summary>
/// Eagerly tails <see cref="IPlayerLogStream"/> at shell startup and maintains
/// the canonical live <see cref="PlayerSkillSnapshot"/> from
/// <c>ProcessLoadSkills</c> (full replace) and <c>ProcessUpdateSkill</c>
/// (per-skill upsert) lines. The single owner of player skill state derived
/// from the log — modules depend on <see cref="IPlayerSkillState"/> rather than
/// re-parsing the stream.
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
/// <para><b>Threading.</b> The snapshot reference is swapped under
/// <see cref="_lock"/>; <see cref="Current"/> reads are lock-free (reference
/// read of an immutable object). <see cref="Subscribe"/> replays and attaches
/// atomically under the same lock the ingestion loop fires under, which closes
/// the late-subscribe race exactly as <c>InventoryService</c> does.</para>
/// </summary>
public sealed class PlayerSkillStateService : BackgroundService, IPlayerSkillState
{
    private readonly IPlayerLogStream _stream;
    private readonly SkillLogParser _parser;
    private readonly IDiagnosticsSink? _diag;

    private readonly object _lock = new();
    private readonly List<Action<PlayerSkillSnapshot>> _handlers = new();
    private volatile PlayerSkillSnapshot _current = PlayerSkillSnapshot.Empty;

    public PlayerSkillStateService(
        IPlayerLogStream stream,
        SkillLogParser parser,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
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
            return new Subscription(this, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Skills", "Subscribing to Player.log for skill-state events");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            switch (_parser.TryParse(raw.Line, raw.Timestamp))
            {
                case SkillsSnapshotEvent snap:
                    ReplaceAll(snap);
                    break;
                case SkillProgressUpdateEvent upd:
                    Upsert(upd);
                    break;
            }
        }
    }

    /// <summary>Wholesale replace from a <c>ProcessLoadSkills</c> dump.</summary>
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
            _current = new PlayerSkillSnapshot(map, snap.Timestamp, SkillStateSource.LiveLog);
            _diag?.Trace("GameState.Skills",
                $"Skill snapshot replaced: {map.Count} skills @ {snap.Timestamp:O}");
            Fire(_current);
        }
    }

    /// <summary>Single-skill upsert from a <c>ProcessUpdateSkill</c> delta.
    /// Accepted even before the first full snapshot — the resulting partial
    /// state is intentional (see the type docs).</summary>
    private void Upsert(SkillProgressUpdateEvent upd)
    {
        lock (_lock)
        {
            // Copy-on-write so any consumer holding the prior snapshot keeps a
            // stable, immutable view.
            var map = new Dictionary<string, SkillProgressSnapshot>(_current.Skills, StringComparer.Ordinal)
            {
                [upd.Skill.SkillKey] = Project(upd.Skill),
            };
            _current = new PlayerSkillSnapshot(map, upd.Timestamp, SkillStateSource.LiveLog);
            _diag?.Trace("GameState.Skills",
                $"Skill upsert: {upd.Skill.SkillKey} raw={upd.Skill.Level} @ {upd.Timestamp:O}");
            Fire(_current);
        }
    }

    private static SkillProgressSnapshot Project(SkillProgressRecord r) => new(
        Level: r.Level,
        BonusLevels: r.BonusLevels,
        XpTowardNextLevel: r.XpTowardNextLevel,
        XpNeededForNextLevel: r.XpNeededForNextLevel,
        MaxLevel: r.MaxLevel);

    /// <summary>MUST be called with <see cref="_lock"/> held.</summary>
    private void Fire(PlayerSkillSnapshot snapshot)
    {
        foreach (var h in _handlers) Invoke(h, snapshot);
    }

    private void Invoke(Action<PlayerSkillSnapshot> handler, PlayerSkillSnapshot snapshot)
    {
        try { handler(snapshot); }
        catch (Exception ex) { _diag?.Warn("GameState.Skills", $"Subscriber threw: {ex.Message}"); }
    }

    private sealed class Subscription : IDisposable
    {
        private PlayerSkillStateService? _owner;
        private readonly Action<PlayerSkillSnapshot> _handler;

        public Subscription(PlayerSkillStateService owner, Action<PlayerSkillSnapshot> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            lock (owner._lock) { owner._handlers.Remove(_handler); }
        }
    }
}
