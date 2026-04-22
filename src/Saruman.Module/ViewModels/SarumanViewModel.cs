using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Saruman.Domain;
using Saruman.Services;

namespace Saruman.ViewModels;

public sealed partial class SarumanViewModel : ObservableObject
{
    private readonly SarumanCodebookService _codebook;

    public SarumanViewModel(SarumanCodebookService codebook)
    {
        _codebook = codebook;

        WordsView = CollectionViewSource.GetDefaultView(Words);
        WordsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(KnownWordRow.TierLabel)));
        WordsView.SortDescriptions.Add(new SortDescription(nameof(KnownWordRow.Tier), ListSortDirection.Ascending));
        WordsView.SortDescriptions.Add(new SortDescription(nameof(KnownWordRow.StateOrder), ListSortDirection.Ascending));
        WordsView.SortDescriptions.Add(new SortDescription(nameof(KnownWordRow.Code), ListSortDirection.Ascending));
        WordsView.Filter = FilterPredicate;

        _codebook.CodebookChanged += (_, _) => Dispatch(Refresh);
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
        var incoming = _codebook.Words;
        var byCode = new Dictionary<string, KnownWord>(incoming.Count, StringComparer.Ordinal);
        foreach (var w in incoming) byCode[w.Code] = w;

        // Update existing rows in-place so selection/scroll state isn't disturbed.
        for (var i = Words.Count - 1; i >= 0; i--)
        {
            var row = Words[i];
            if (byCode.TryGetValue(row.Code, out var w))
            {
                row.UpdateFrom(w);
                byCode.Remove(row.Code);
            }
            else
            {
                Words.RemoveAt(i);
            }
        }
        foreach (var w in byCode.Values)
            Words.Add(new KnownWordRow(w));

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
        WordOfPowerTier? lastTier = null;
        foreach (var row in Words
            .Where(r => r.IsKnown)
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.Code, StringComparer.Ordinal))
        {
            if (lastTier is not null && lastTier != row.Tier) sb.AppendLine();
            lastTier = row.Tier;
            sb.Append(row.Code).Append(" — ").AppendLine(row.EffectName);
        }
        if (sb.Length == 0) return;
        TrySetClipboard(sb.ToString().TrimEnd());
    }

    [RelayCommand]
    private void MarkSpent(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        _codebook.MarkSpent(code, DateTime.UtcNow);
    }

    [RelayCommand]
    private void MarkKnown(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        _codebook.MarkKnown(code);
    }

    [RelayCommand]
    private void RemoveWord(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        _codebook.Remove(code);
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
