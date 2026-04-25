using Mithril.Shared.Character;
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
    public void RecordDiscovery(WordOfPowerDiscovered evt)
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
            Persist();
        }
        CodebookChanged?.Invoke(this, EventArgs.Empty);
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
