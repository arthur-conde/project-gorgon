using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.GameState.WordsOfPower;
using Saruman.Services;

namespace Saruman.ViewModels;

/// <summary>
/// Saruman's words-of-power view-model (#603 — post-codebook-split). Composes
/// <see cref="IWordOfPowerView"/> (cross-source view) with
/// <see cref="SarumanOverrideService"/> (module-internal user override). The
/// VM never mutates discovery state — that is canonically owned by the view —
/// and never clears the view's monotonic Spent flag.
///
/// <para>Refresh policy: subscribes to both the view's <c>CodebookChanged</c>
/// event and the override service's <c>OverridesChanged</c> event;
/// hops onto the UI dispatcher and rebuilds the row collection in-place to
/// preserve selection / scroll.</para>
/// </summary>
public sealed partial class SarumanViewModel : ObservableObject
{
    private readonly IWordOfPowerView _view;
    private readonly SarumanOverrideService _overrides;

    public SarumanViewModel(IWordOfPowerView view, SarumanOverrideService overrides)
    {
        _view = view;
        _overrides = overrides;

        WordsView = CollectionViewSource.GetDefaultView(Words);
        WordsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(KnownWordRow.EffectName)));
        WordsView.SortDescriptions.Add(new SortDescription(nameof(KnownWordRow.EffectName), ListSortDirection.Ascending));
        WordsView.SortDescriptions.Add(new SortDescription(nameof(KnownWordRow.StateOrder), ListSortDirection.Ascending));
        WordsView.SortDescriptions.Add(new SortDescription(nameof(KnownWordRow.Code), ListSortDirection.Ascending));
        WordsView.Filter = FilterPredicate;

        _view.CodebookChanged += (_, _) => Dispatch(Refresh);
        _overrides.OverridesChanged += (_, _) => Dispatch(Refresh);
        Refresh();
    }

    public ObservableCollection<KnownWordRow> Words { get; } = [];
    public ICollectionView WordsView { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasData))]
    private int _trackedCount;

    [ObservableProperty] private int _spentCount;

    public bool HasData => TrackedCount + SpentCount > 0;

    [ObservableProperty] private bool _hideSpent = true;

    partial void OnHideSpentChanged(bool value) => WordsView.Refresh();

    private bool FilterPredicate(object o)
    {
        if (o is not KnownWordRow row) return false;
        if (HideSpent && row.IsSpent) return false;
        return true;
    }

    private void Refresh()
    {
        var entries = _view.Entries;
        var byCode = new Dictionary<string, WordOfPowerEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var e in entries) byCode[e.Code] = e;

        // Update existing rows in-place so selection/scroll state isn't disturbed.
        for (var i = Words.Count - 1; i >= 0; i--)
        {
            var row = Words[i];
            if (byCode.TryGetValue(row.Code, out var entry))
            {
                row.UpdateFrom(entry, _overrides.IsSpent(entry.Code));
                byCode.Remove(row.Code);
            }
            else
            {
                Words.RemoveAt(i);
            }
        }
        foreach (var e in byCode.Values)
        {
            Words.Add(new KnownWordRow(e, _overrides.IsSpent(e.Code)));
        }

        var known = 0;
        var spent = 0;
        foreach (var r in Words)
        {
            if (r.IsSpent) spent++; else known++;
        }
        TrackedCount = known;
        SpentCount = spent;

        WordsView.Refresh();
    }

    [RelayCommand]
    private void CopyCode(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        TrySetClipboard(code);
    }

    [RelayCommand]
    private void CopyAllTracked()
    {
        var sb = new StringBuilder();
        string? lastEffect = null;
        foreach (var row in Words
            .Where(r => r.IsKnown)
            .OrderBy(r => r.EffectName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Code, StringComparer.Ordinal))
        {
            if (lastEffect is not null && !string.Equals(lastEffect, row.EffectName, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine();
            lastEffect = row.EffectName;
            sb.Append(row.Code).Append(" — ").AppendLine(row.EffectName);
        }
        if (sb.Length == 0) return;
        TrySetClipboard(sb.ToString().TrimEnd());
    }

    [RelayCommand]
    private void MarkSpent(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        _overrides.MarkSpent(code);
    }

    [RelayCommand]
    private void ClearOverride(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        _overrides.ClearOverride(code);
    }

    private static void TrySetClipboard(string text)
    {
        try { Clipboard.SetText(text); }
        catch { /* clipboard can transiently fail; user can retry */ }
    }

    private static void Dispatch(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}
