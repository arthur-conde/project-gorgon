using Mithril.Shared.Character;
using Mithril.Shared.Logging;
using Saruman.Domain;
using Saruman.Settings;

namespace Saruman.Services;

/// <summary>
/// Owns the Words-of-Power codebook for the currently active character. Reads and writes
/// through <see cref="PerCharacterView{T}"/>; on a character switch the codebook swaps
/// to the new character's file and <see cref="CodebookChanged"/> fires so the VM re-renders.
/// Mutations no-op when no character is active.
/// </summary>
public sealed class SarumanCodebookService
{
    private readonly PerCharacterView<SarumanState> _view;
    private readonly Lock _lock = new();

    public SarumanCodebookService(PerCharacterView<SarumanState> view)
    {
        _view = view;
        _view.CurrentChanged += (_, _) => CodebookChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CodebookChanged;

    public IReadOnlyCollection<KnownWord> Words
    {
        get
        {
            var state = _view.Current;
            if (state is null) return [];
            lock (_lock) return state.Codebook.Values.ToArray();
        }
    }

    public bool IsTracked(string code)
    {
        var state = _view.Current;
        if (state is null) return false;
        lock (_lock) return state.Codebook.ContainsKey(code);
    }

    public KnownWord? TryGet(string code)
    {
        var state = _view.Current;
        if (state is null) return null;
        lock (_lock) return state.Codebook.GetValueOrDefault(code);
    }

    /// <summary>
    /// Record a discovery event. If the code is new, add it. If it already exists, bump
    /// <see cref="KnownWord.DiscoveryCount"/>; re-discovery of a spent word flips it
    /// back to <see cref="WordOfPowerState.Known"/>. No-op when no character is active.
    /// </summary>
    /// <param name="sequence">
    /// Optional <c>LocalPlayerLogLine.Sequence</c> of the originating log envelope.
    /// When supplied, the active character's
    /// <see cref="SarumanState.DiscoveryHighWaterSequence"/> is advanced to
    /// <c>max(prior, sequence)</c> as part of the same persist. The discovery
    /// ingestion service supplies this; tests / chat-driven callers may pass
    /// null and the high-water is left untouched.
    /// </param>
    public void RecordDiscovery(WordOfPowerDiscovered evt, long? sequence = null)
    {
        var state = _view.Current;
        if (state is null) return;
        lock (_lock)
        {
            if (state.Codebook.TryGetValue(evt.Code, out var existing))
            {
                existing.DiscoveryCount++;
                existing.EffectName = evt.EffectName;
                existing.Description = evt.Description;
                if (existing.State == WordOfPowerState.Spent)
                {
                    existing.State = WordOfPowerState.Known;
                    existing.SpentAt = null;
                }
            }
            else
            {
                state.Codebook[evt.Code] = new KnownWord
                {
                    Code = evt.Code,
                    EffectName = evt.EffectName,
                    Description = evt.Description,
                    FirstDiscoveredAt = evt.Timestamp,
                };
            }
            if (sequence is long s && (state.DiscoveryHighWaterSequence is not long prior || s > prior))
            {
                state.DiscoveryHighWaterSequence = s;
            }
            Persist();
        }
        CodebookChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Read the persisted high-water Player.log sequence for the active
    /// character — what the L1 driver should pass as
    /// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/> on the
    /// discovery subscription so replayed envelopes whose sequence is
    /// <c>&lt;= HighWater</c> are dropped before the handler runs and the
    /// monotonic <see cref="KnownWord.DiscoveryCount"/> doesn't re-inflate
    /// across a Mithril restart. Returns null when no character is active
    /// or the character has never recorded a discovery.
    /// </summary>
    public long? DiscoveryHighWaterSequence
    {
        get
        {
            var state = _view.Current;
            if (state is null) return null;
            lock (_lock) return state.DiscoveryHighWaterSequence;
        }
    }

    public bool MarkSpent(string code, DateTime spokenAt)
    {
        var state = _view.Current;
        if (state is null) return false;
        bool changed;
        lock (_lock)
        {
            if (!state.Codebook.TryGetValue(code, out var w)) return false;
            if (w.State == WordOfPowerState.Spent) return false;
            w.State = WordOfPowerState.Spent;
            w.SpentAt = spokenAt;
            Persist();
            changed = true;
        }
        if (changed) CodebookChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public bool MarkKnown(string code)
    {
        var state = _view.Current;
        if (state is null) return false;
        bool changed;
        lock (_lock)
        {
            if (!state.Codebook.TryGetValue(code, out var w)) return false;
            if (w.State == WordOfPowerState.Known) return false;
            w.State = WordOfPowerState.Known;
            w.SpentAt = null;
            Persist();
            changed = true;
        }
        if (changed) CodebookChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public bool Remove(string code)
    {
        var state = _view.Current;
        if (state is null) return false;
        bool changed;
        lock (_lock)
        {
            changed = state.Codebook.Remove(code);
            if (changed) Persist();
        }
        if (changed) CodebookChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    private void Persist()
    {
        try { _view.Save(); }
        catch { /* best-effort persistence */ }
    }
}
