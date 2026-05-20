using Mithril.Shared.Character;

namespace Samwise.State;

/// <summary>
/// Persists the GardenStateMachine to per-character files. Each character's plot dict
/// lives in <c>characters/{slug}/samwise.json</c>. The state machine still holds every
/// known character's plots in memory (the garden view shows them all), but writes are
/// scoped to just the character(s) touched by recent events.
///
/// Subscribes to <see cref="GardenStateMachine.PlotChanged"/>/<c>PlotsRemoved</c> with
/// a 500 ms debounce; on every tick, saves only the characters flagged dirty.
///
/// <para><b>L1 high-water persistence (#550 PR 3).</b> Samwise rebuilds its plot state
/// at gate-open by hydrating from disk, then the L1 driver replays the entire session on
/// the LocalPlayer pipe — without a high-water, plant / <c>UpdateDescription</c> /
/// <c>StartInteraction</c> events would re-apply on top of already-persisted plots,
/// advancing stages and burning slot caps that were already counted. We persist the
/// largest applied L0 <c>Sequence</c> per character via
/// <see cref="GardenCharacterState.HighWaterSequence"/>, fed back to L1's
/// <c>SkipProcessedHighWater</c> on the next session's subscribe. Per-char storage
/// matches the existing file layout — the value advances in lockstep across characters
/// (the L1 stream serves every character on this Mithril instance), so consumers read
/// it via <see cref="LoadAllAsync"/> and pass the min through L1.</para>
/// </summary>
public sealed class GardenStateService : IDisposable
{
    private readonly GardenStateMachine _state;
    private readonly PerCharacterStore<GardenCharacterState> _store;
    private readonly IActiveCharacterService _active;
    private readonly System.Timers.Timer _debounce;
    private readonly HashSet<string> _dirtyChars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>
    /// Latest in-memory high-water — updated by
    /// <see cref="GardenIngestionService"/> after each successful
    /// <see cref="GardenStateMachine.Apply"/>, flushed to every known
    /// character's file on the debounce tick. Read via
    /// <see cref="CurrentHighWater"/>.
    /// </summary>
    private long _highWaterSequence;

    public GardenStateService(
        GardenStateMachine state,
        PerCharacterStore<GardenCharacterState> store,
        IActiveCharacterService active)
    {
        _state = state;
        _store = store;
        _active = active;
        _debounce = new System.Timers.Timer(500) { AutoReset = false };
        _debounce.Elapsed += (_, _) => Flush();
        _state.PlotChanged += OnChanged;
        _state.PlotsRemoved += OnRemoved;
    }

    /// <summary>
    /// Snapshot of the current in-memory high-water sequence. Reads are
    /// lock-free via <see cref="Interlocked.Read"/>; the L1 driver only
    /// needs this at subscribe-time, but the field is the source of truth
    /// after that point.
    /// </summary>
    public long CurrentHighWater => Interlocked.Read(ref _highWaterSequence);

    /// <summary>
    /// Advance the high-water past <paramref name="sequence"/> after a
    /// successful <see cref="GardenStateMachine.Apply"/>. Idempotent
    /// (<see cref="Math.Max(long, long)"/> semantics) — out-of-order
    /// sequences cannot regress the high-water. Marks every known
    /// character dirty so the next debounce tick persists the advance
    /// across the per-char fleet.
    /// </summary>
    public void AdvanceHighWater(long sequence)
    {
        // Lock-free CAS loop: only update if the candidate is strictly larger.
        while (true)
        {
            var current = Interlocked.Read(ref _highWaterSequence);
            if (sequence <= current) return;
            if (Interlocked.CompareExchange(ref _highWaterSequence, sequence, current) == current)
                break;
        }
        // Touch every known character so the new high-water flushes on the
        // next debounce tick. Plots-only mutations already mark the owning
        // char dirty via OnChanged; this covers events that advance the
        // cursor without mutating any plot.
        lock (_gate)
        {
            foreach (var (charName, _) in _state.Snapshot())
                if (!string.IsNullOrEmpty(charName))
                    _dirtyChars.Add(charName);
        }
        MarkDirty();
    }

    /// <summary>
    /// Read every known character's per-char file from disk. The returned map is handed
    /// to <see cref="GardenStateMachine.HydrateCharacter"/> by the caller — the caller
    /// must invoke that on the WPF thread since it raises <c>PlotChanged</c> and mutates
    /// the VM's bound collection.
    /// </summary>
    /// <returns>
    /// <para>The per-character loaded state plus the <see cref="long"/> high-water
    /// the caller should feed to L1's <c>SkipProcessedHighWater</c> filter.</para>
    /// <para>The high-water reported is <c>min</c> across every loaded character's
    /// <see cref="GardenCharacterState.HighWaterSequence"/> (taking 0 when no
    /// characters have a file yet). <c>min</c> is the correct shape: a character whose
    /// persisted high-water is below the others must still receive the events it
    /// missed, so the filter is only as aggressive as the most-behind character. The
    /// L1 driver still delivers per-Sequence; <see cref="AdvanceHighWater"/> picks up
    /// from there.</para>
    /// </returns>
    public Task<(IReadOnlyList<(string CharName, IReadOnlyDictionary<string, PersistedPlot> Plots)> Characters, long HighWater)>
        LoadAllAsync(CancellationToken ct = default)
    {
        var result = new List<(string, IReadOnlyDictionary<string, PersistedPlot>)>();
        long? minHighWater = null;
        foreach (var snapshot in _active.Characters)
        {
            if (string.IsNullOrEmpty(snapshot.Name) || string.IsNullOrEmpty(snapshot.Server)) continue;
            var perChar = _store.Load(snapshot.Name, snapshot.Server);
            result.Add((snapshot.Name, perChar.Plots));
            minHighWater = minHighWater is null
                ? perChar.HighWaterSequence
                : Math.Min(minHighWater.Value, perChar.HighWaterSequence);
        }
        // Seed the in-memory cursor from disk so subsequent AdvanceHighWater
        // calls only ever push it forward. We use min across characters as
        // the conservative restart shape — the L1 filter shouldn't elide
        // events any one character still needs (see <returns> above).
        var loaded = minHighWater ?? 0L;
        // Same Max-only contract as AdvanceHighWater: cold-start LoadAll
        // must not regress the cursor below whatever AdvanceHighWater
        // already pushed (it never has at this point, but the invariant
        // is cheap to preserve).
        while (true)
        {
            var current = Interlocked.Read(ref _highWaterSequence);
            if (loaded <= current) break;
            if (Interlocked.CompareExchange(ref _highWaterSequence, loaded, current) == current) break;
        }
        return Task.FromResult<(IReadOnlyList<(string, IReadOnlyDictionary<string, PersistedPlot>)>, long)>(
            (result, loaded));
    }

    private void OnChanged(object? sender, PlotChangedArgs e)
    {
        if (string.IsNullOrEmpty(e.Plot.CharName)) return;
        lock (_gate) _dirtyChars.Add(e.Plot.CharName);
        MarkDirty();
    }

    private void OnRemoved(object? sender, EventArgs e)
    {
        // On removal we don't know which characters lost plots; flag them all (via snapshot).
        foreach (var (charName, _) in _state.Snapshot())
        {
            if (!string.IsNullOrEmpty(charName)) lock (_gate) _dirtyChars.Add(charName);
        }
        MarkDirty();
    }

    private void MarkDirty()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Flush()
    {
        string[] toFlush;
        lock (_gate)
        {
            if (_dirtyChars.Count == 0) return;
            toFlush = _dirtyChars.ToArray();
            _dirtyChars.Clear();
        }

        var snapshot = _state.Snapshot();
        var serverByName = ResolveServers();
        var highWater = Interlocked.Read(ref _highWaterSequence);
        foreach (var charName in toFlush)
        {
            if (!serverByName.TryGetValue(charName, out var server)) continue;
            snapshot.TryGetValue(charName, out var plots);
            var perChar = new GardenCharacterState
            {
                Plots = plots is null
                    ? new Dictionary<string, PersistedPlot>(StringComparer.Ordinal)
                    : plots.ToDictionary(kv => kv.Key, kv => new PersistedPlot
                    {
                        CropType = kv.Value.CropType,
                        Stage = kv.Value.Stage,
                        Title = kv.Value.Title,
                        Description = kv.Value.Description,
                        Action = kv.Value.Action,
                        Scale = kv.Value.Scale,
                        PlantedAt = kv.Value.PlantedAt,
                        UpdatedAt = kv.Value.UpdatedAt,
                        PausedSince = kv.Value.PausedSince,
                        PausedDuration = kv.Value.PausedDuration,
                    }, StringComparer.Ordinal),
                HighWaterSequence = highWater,
            };
            try { _store.Save(charName, server, perChar); } catch { /* best-effort */ }
        }
    }

    private Dictionary<string, string> ResolveServers()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snap in _active.Characters)
        {
            if (!map.ContainsKey(snap.Name)) map[snap.Name] = snap.Server;
        }
        if (!string.IsNullOrEmpty(_active.ActiveCharacterName) && !string.IsNullOrEmpty(_active.ActiveServer))
            map[_active.ActiveCharacterName] = _active.ActiveServer;
        return map;
    }

    public void Dispose()
    {
        _state.PlotChanged -= OnChanged;
        _state.PlotsRemoved -= OnRemoved;
        _debounce.Stop();
        _debounce.Dispose();
        Flush();
    }
}
