using Mithril.Shared.Character;
using Saruman.Settings;

namespace Saruman.Services;

/// <summary>
/// Saruman's module-internal user-override ledger (#603 — post-codebook-split).
/// One-way Sticky Spent: the user marks a code Spent manually (for offline
/// burns the view didn't observe), the override persists per-character. There
/// is no inverse "clear" — monotonic Spent makes Known-override mechanically
/// meaningless (per #603 spec).
///
/// <para>Consumed by <see cref="ViewModels.SarumanViewModel"/> to compose
/// <c>isSpent = view.IsSpent(code) || override.IsSpent(code)</c>. Not exposed
/// as a cross-module interface — there is no read-side use case outside
/// Saruman itself today; YAGNI.</para>
///
/// <para>Persists to the per-character module store (<c>saruman.json</c>) via
/// <see cref="PerCharacterView{T}"/>. Mutations no-op when no character is
/// active.</para>
/// </summary>
public sealed class SarumanOverrideService
{
    private readonly PerCharacterView<SarumanState> _view;
    private readonly Lock _lock = new();

    public SarumanOverrideService(PerCharacterView<SarumanState> view)
    {
        _view = view;
        _view.CurrentChanged += (_, _) => OverridesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Fires on mutation or character switch.</summary>
    public event EventHandler? OverridesChanged;

    /// <summary>
    /// <c>true</c> if the user has marked this code Spent manually for the
    /// active character; <c>false</c> otherwise (including when no character
    /// is active).
    /// </summary>
    public bool IsSpent(string code)
    {
        var state = _view.Current;
        if (state is null) return false;
        lock (_lock) return state.SpentOverrides.Contains(code);
    }

    /// <summary>
    /// Mark the code Spent manually. No-op if already marked or no character
    /// is active. Returns <c>true</c> only when the override is newly added.
    /// </summary>
    public bool MarkSpent(string code)
    {
        var state = _view.Current;
        if (state is null) return false;
        bool added;
        lock (_lock)
        {
            added = state.SpentOverrides.Add(code);
            if (added) Persist();
        }
        if (added) OverridesChanged?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <summary>
    /// Remove the override (returns the code to "view's opinion" — Spent only
    /// if the view itself observed a chat burn). Provided so the UI can offer
    /// a "undo my manual mark" affordance for a Sticky Spent the user added by
    /// mistake. NOT a Known-override surface — the underlying view's Spent
    /// state is still monotonic.
    /// </summary>
    public bool ClearOverride(string code)
    {
        var state = _view.Current;
        if (state is null) return false;
        bool removed;
        lock (_lock)
        {
            removed = state.SpentOverrides.Remove(code);
            if (removed) Persist();
        }
        if (removed) OverridesChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    private void Persist()
    {
        try { _view.Save(); }
        catch { /* best-effort */ }
    }
}
