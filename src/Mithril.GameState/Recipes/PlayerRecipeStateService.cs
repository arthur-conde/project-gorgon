using Microsoft.Extensions.Hosting;
using Mithril.GameState.Recipes.Parsing;
using Mithril.Shared.Diagnostics;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Recipes;

/// <summary>
/// Eagerly tails <see cref="IPlayerLogStream"/> at shell startup and maintains
/// the canonical live <see cref="PlayerRecipeSnapshot"/> from
/// <c>ProcessLoadRecipes</c> (full replace) and <c>ProcessUpdateRecipe</c>
/// (per-recipe upsert) lines. The single owner of player recipe state derived
/// from the log — modules depend on <see cref="IPlayerRecipeState"/> rather
/// than re-parsing the stream or requiring a character re-export.
///
/// <para><b>Self-heal / warm-up.</b> <c>ProcessLoadRecipes</c> is emitted at
/// login and again on every zone / session transition, so a wholesale replace
/// on each one keeps state correct (including never-crafted known recipes)
/// even when Mithril starts tailing mid-session. Before the first snapshot of
/// the session, <see cref="Current"/> is
/// <see cref="PlayerRecipeSnapshot.Empty"/>; isolated
/// <c>ProcessUpdateRecipe</c> lines seen first produce a deliberately partial
/// snapshot (better than nothing — the next <c>ProcessLoadRecipes</c> makes it
/// whole). This window is the documented contract.</para>
///
/// <para><b>Threading.</b> The snapshot reference is swapped under
/// <see cref="_lock"/>; <see cref="Current"/> reads are lock-free (reference
/// read of an immutable object). <see cref="Subscribe"/> replays and attaches
/// atomically under the same lock the ingestion loop fires under, which closes
/// the late-subscribe race exactly as <c>PlayerSkillStateService</c> does.</para>
/// </summary>
public sealed class PlayerRecipeStateService : BackgroundService, IPlayerRecipeState
{
    private readonly IPlayerLogStream _stream;
    private readonly RecipeLogParser _parser;
    private readonly IDiagnosticsSink? _diag;
    private readonly ThrottledWarn _warn;

    private readonly object _lock = new();
    private readonly List<Action<PlayerRecipeSnapshot>> _handlers = new();
    private readonly List<Action<RecipeChange>> _changeHandlers = new();
    private volatile PlayerRecipeSnapshot _current = PlayerRecipeSnapshot.Empty;

    public PlayerRecipeStateService(
        IPlayerLogStream stream,
        RecipeLogParser parser,
        IDiagnosticsSink? diag = null)
    {
        _stream = stream;
        _parser = parser;
        _diag = diag;
        _warn = new ThrottledWarn(diag, "GameState.Recipes");
    }

    public PlayerRecipeSnapshot Current => _current;

    public IDisposable Subscribe(Action<PlayerRecipeSnapshot> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            Invoke(handler, _current);
            _handlers.Add(handler);
            return new Unsub(this, _handlers, handler);
        }
    }

    public IDisposable SubscribeChanges(Action<RecipeChange> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            // No replay — a RecipeChange is an event, not state. Current state
            // is read via Current.
            _changeHandlers.Add(handler);
            return new Unsub(this, _changeHandlers, handler);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _diag?.Info("GameState.Recipes", "Subscribing to Player.log for recipe-state events");
        await foreach (var raw in _stream.SubscribeAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                switch (_parser.TryParse(raw.Line, raw.Timestamp))
                {
                    case RecipesSnapshotEvent snap:
                        ReplaceAll(snap);
                        break;
                    case RecipeUpdateEvent upd:
                        Upsert(upd);
                        break;
                }
            }
            catch (Exception ex) { _warn.Warn($"Ingestion error: {ex.Message}"); }
        }
    }

    /// <summary>Wholesale replace from a <c>ProcessLoadRecipes</c> dump. Emits a
    /// <see cref="RecipeChangeKind.SnapshotReplace"/> only for recipes whose
    /// projection actually differs from (or is new vs.) the prior state — a
    /// no-op re-sync produces no change events.</summary>
    private void ReplaceAll(RecipesSnapshotEvent snap)
    {
        var map = new Dictionary<int, RecipeProgressSnapshot>(snap.Recipes.Count);
        foreach (var r in snap.Recipes)
        {
            // Last-wins on the (not observed in practice) chance of a dup id —
            // the indexer rather than Add avoids throwing on grammar drift.
            map[r.RecipeId] = Project(r);
        }

        lock (_lock)
        {
            var prev = _current.Recipes;
            List<RecipeChange>? changes = _changeHandlers.Count == 0 ? null : new();
            if (changes is not null)
            {
                foreach (var (id, cur) in map)
                {
                    bool had = prev.TryGetValue(id, out var before);
                    if (had && before.Equals(cur)) continue; // unchanged — skip
                    changes.Add(new RecipeChange(
                        id, had ? before : null, cur, CompletionsGained: 0,
                        RecipeChangeKind.SnapshotReplace, snap.Timestamp));
                }
            }

            _current = new PlayerRecipeSnapshot(map, snap.Timestamp, RecipeStateSource.LiveLog);
            _diag?.Trace("GameState.Recipes",
                $"Recipe snapshot replaced: {map.Count} recipes @ {snap.Timestamp:O}");
            Fire(_current);
            if (changes is not null)
                foreach (var c in changes) FireChange(c);
        }
    }

    /// <summary>Single-recipe upsert from a <c>ProcessUpdateRecipe</c> delta.
    /// Accepted even before the first full snapshot — the resulting partial
    /// state is intentional (see the type docs). Classifies the change as a
    /// <see cref="RecipeChangeKind.Learned"/> (newly known, count 0) or a
    /// <see cref="RecipeChangeKind.Completed"/> (count increased); an
    /// idempotent re-emit that moves nothing is dropped entirely (PG re-emits
    /// learn/count lines — unlike <c>ProcessUpdateSkill</c>, which only fires on
    /// real movement, so the skill tracker need not guard this).</summary>
    private void Upsert(RecipeUpdateEvent upd)
    {
        lock (_lock)
        {
            var id = upd.Recipe.RecipeId;
            RecipeProgressSnapshot? before =
                _current.Recipes.TryGetValue(id, out var b) ? b : null;
            var cur = Project(upd.Recipe);

            // True no-op (already known at this exact count — a duplicate
            // learn/count re-emit): nothing changed, so don't churn the
            // snapshot or fire spurious events.
            if (before is { } bv && bv.Equals(cur)) return;

            RecipeChangeKind kind;
            int gained;
            if (before is null)
            {
                // No baseline. count 0 = a fresh learn; count >=1 = a craft we
                // joined mid-history (don't attribute a gain — honest contract).
                kind = cur.Completions == 0 ? RecipeChangeKind.Learned : RecipeChangeKind.Completed;
                gained = 0;
            }
            else
            {
                // Known already; the count moved. Counts are monotonic in
                // practice — a decrease (grammar drift) still upserts last-wins
                // but reports no gain.
                kind = RecipeChangeKind.Completed;
                gained = Math.Max(0, cur.Completions - before.Value.Completions);
            }

            // Copy-on-write so any consumer holding the prior snapshot keeps a
            // stable, immutable view.
            var map = new Dictionary<int, RecipeProgressSnapshot>(_current.Recipes)
            {
                [id] = cur,
            };
            _current = new PlayerRecipeSnapshot(map, upd.Timestamp, RecipeStateSource.LiveLog);
            _diag?.Trace("GameState.Recipes",
                $"Recipe upsert: {id} completions={cur.Completions} ({kind}) @ {upd.Timestamp:O}");
            Fire(_current);
            FireChange(new RecipeChange(id, before, cur, gained, kind, upd.Timestamp));
        }
    }

    private static RecipeProgressSnapshot Project(RecipeCompletionRecord r) =>
        new(Completions: r.Completions);

    /// <summary>MUST be called with <see cref="_lock"/> held.</summary>
    private void Fire(PlayerRecipeSnapshot snapshot)
    {
        foreach (var h in _handlers) Invoke(h, snapshot);
    }

    /// <summary>MUST be called with <see cref="_lock"/> held.</summary>
    private void FireChange(RecipeChange change)
    {
        foreach (var h in _changeHandlers) InvokeChange(h, change);
    }

    private void Invoke(Action<PlayerRecipeSnapshot> handler, PlayerRecipeSnapshot snapshot)
    {
        try { handler(snapshot); }
        catch (Exception ex) { _diag?.Warn("GameState.Recipes", $"Subscriber threw: {ex.Message}"); }
    }

    private void InvokeChange(Action<RecipeChange> handler, RecipeChange change)
    {
        try { handler(change); }
        catch (Exception ex) { _diag?.Warn("GameState.Recipes", $"Change subscriber threw: {ex.Message}"); }
    }

    /// <summary>
    /// Removes <paramref name="handler"/> from the list it was added to, under
    /// the owner's lock. One implementation serves both the snapshot and the
    /// change channels.
    /// </summary>
    private sealed class Unsub : IDisposable
    {
        private PlayerRecipeStateService? _owner;
        private readonly System.Collections.IList _list;
        private readonly object _handler;

        public Unsub(PlayerRecipeStateService owner, System.Collections.IList list, object handler)
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
