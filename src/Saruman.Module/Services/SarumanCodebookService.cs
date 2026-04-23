using Gorgon.Shared.Settings;
using Saruman.Domain;
using Saruman.Settings;

namespace Saruman.Services;

public sealed class SarumanCodebookService
{
    private readonly ISettingsStore<SarumanState> _store;
    private readonly SarumanState _state;
    private readonly object _lock = new();

    public SarumanCodebookService(ISettingsStore<SarumanState> store)
    {
        _store = store;
        _state = store.Load();
    }

    public event EventHandler? CodebookChanged;

    public IReadOnlyCollection<KnownWord> Words
    {
        get
        {
            lock (_lock)
            {
                return _state.Codebook.Values.ToArray();
            }
        }
    }

    public bool IsTracked(string code)
    {
        lock (_lock) return _state.Codebook.ContainsKey(code);
    }

    public KnownWord? TryGet(string code)
    {
        lock (_lock) return _state.Codebook.GetValueOrDefault(code);
    }

    /// <summary>
    /// Record a discovery event. If the code is new, add it. If it already
    /// exists, bump <see cref="KnownWord.DiscoveryCount"/>; re-discovery of a
    /// spent word flips it back to <see cref="WordOfPowerState.Known"/>.
    /// </summary>
    public void RecordDiscovery(WordOfPowerDiscovered evt)
    {
        lock (_lock)
        {
            if (_state.Codebook.TryGetValue(evt.Code, out var existing))
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
                _state.Codebook[evt.Code] = new KnownWord
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
        bool changed;
        lock (_lock)
        {
            if (!_state.Codebook.TryGetValue(code, out var w)) return false;
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
        bool changed;
        lock (_lock)
        {
            if (!_state.Codebook.TryGetValue(code, out var w)) return false;
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
        bool changed;
        lock (_lock)
        {
            changed = _state.Codebook.Remove(code);
            if (changed) Persist();
        }
        if (changed) CodebookChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    private void Persist()
    {
        try { _store.Save(_state); }
        catch { /* best-effort persistence */ }
    }
}
