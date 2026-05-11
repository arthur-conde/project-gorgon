using System.Collections.ObjectModel;
using System.Windows.Threading;
using Arwen.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Mithril.Shared.Wpf.Dialogs;

namespace Arwen.ViewModels;

/// <summary>Row for the per-(NPC, item) rates grid.</summary>
public sealed class ItemRateRow
{
    public required string NpcName { get; init; }
    public required string ItemName { get; init; }
    public required double Rate { get; init; }
    public required int SampleCount { get; init; }
    public required double MinRate { get; init; }
    public required double MaxRate { get; init; }
}

/// <summary>Row for the per-(NPC, preference-signature) rates grid.</summary>
public sealed class SignatureRateRow
{
    public required string NpcName { get; init; }
    public required string Signature { get; init; }
    public required double Rate { get; init; }
    public required int SampleCount { get; init; }
    public required double MinRate { get; init; }
    public required double MaxRate { get; init; }
}

/// <summary>Row for the per-NPC baseline rates grid.</summary>
public sealed class NpcBaselineRow
{
    public required string NpcName { get; init; }
    public required double Rate { get; init; }
    public required int SampleCount { get; init; }
    public required double MinRate { get; init; }
    public required double MaxRate { get; init; }
}

/// <summary>
/// Row for the raw observations grid and the editable observations tab. Display fields
/// (NpcName/ItemName/Signature/ItemValue/EffectivePref/FavorDelta/DerivedRate/Timestamp)
/// are init-only — they're built from the underlying <see cref="GiftObservation"/> on every
/// <see cref="CalibrationViewModel.Refresh"/>. Only <see cref="Quantity"/> is observable so
/// the editor TextBox can two-way bind; the canonical Quantity lives on the underlying
/// observation and only changes when <see cref="CalibrationService.UpdateObservationQuantity"/>
/// succeeds. <see cref="OriginalQuantity"/> is the value we revert to if the commit fails.
/// </summary>
public sealed partial class ObservationRow : ObservableObject
{
    public required string ObservationKey { get; init; }
    public required string NpcName { get; init; }
    public required string ItemName { get; init; }
    public required int IconId { get; init; }
    public required string Signature { get; init; }
    public required IReadOnlyList<MatchedPreference> MatchedPreferences { get; init; }
    public required double ItemValue { get; init; }
    public required double EffectivePref { get; init; }
    public required double FavorDelta { get; init; }
    public required double DerivedRate { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required int MaxStackSize { get; init; }
    public required int OriginalQuantity { get; init; }
    public required ObservationFlag Flag { get; init; }
    public required string? FlagTooltip { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsQuantityValid))]
    [NotifyPropertyChangedFor(nameof(IsQuantityEdited))]
    [NotifyPropertyChangedFor(nameof(PreviewedRate))]
    [NotifyPropertyChangedFor(nameof(TotalValue))]
    private int _quantity;

    public bool IsQuantityValid => Quantity >= 1 && Quantity <= MaxStackSize;

    public bool IsQuantityEditable => MaxStackSize > 1;

    public bool IsFlagged => Flag != ObservationFlag.None;

    /// <summary>True when the user has typed a different Quantity than what's persisted.</summary>
    public bool IsQuantityEdited => Quantity != OriginalQuantity;

    /// <summary>
    /// Total gold worth of the gifted stack: <see cref="ItemValue"/> × <see cref="Quantity"/>.
    /// Live: follows the in-progress edit so the preview moves while the user adjusts Quantity.
    /// Note this is the stack's market value, not the calibration "score" — Arwen's GiftScanner
    /// "Score" column (<see cref="Arwen.Domain.GiftMatch.RelativeScore"/>) is <c>Pref × ItemValue</c>;
    /// the rate-denominator factor (<c>EffectivePref × ItemValue × Quantity</c>) isn't surfaced
    /// directly because the headline Rate already conveys it.
    /// </summary>
    public double TotalValue => ItemValue * Quantity;

    /// <summary>
    /// Live preview of <see cref="DerivedRate"/> using the current edit-in-progress
    /// <see cref="Quantity"/>. Equals <see cref="DerivedRate"/> when nothing's been edited.
    /// Same formula as <see cref="GiftObservation.DerivedRate"/>:
    /// <c>FavorDelta / (EffectivePref × ItemValue × Quantity)</c>.
    /// </summary>
    public double PreviewedRate =>
        EffectivePref == 0 || ItemValue == 0 || Quantity == 0
            ? 0
            : FavorDelta / (EffectivePref * ItemValue * Quantity);

    public void RevertQuantity() => Quantity = OriginalQuantity;
}

/// <summary>
/// Row for the "Pending observations" list. Forwards <see cref="Quantity"/>
/// two-way to the underlying <see cref="PendingGiftObservation"/> so the
/// user's edit lands on the canonical entry that <c>ConfirmPending</c> reads.
/// <see cref="ExpiresIn"/> is recomputed by the VM on a 30s tick.
/// </summary>
public sealed partial class PendingObservationRow : ObservableObject
{
    public required PendingGiftObservation Source { get; init; }
    public required string NpcName { get; init; }
    public required TimeSpan Ttl { get; init; }

    public Guid Id => Source.Id;
    public string DisplayName => Source.DisplayName;
    public int IconId => Source.IconId;
    public double FavorDelta => Source.FavorDelta;
    public int MaxStackSize => Source.MaxStackSize;
    public DateTimeOffset Timestamp => Source.Timestamp;

    public int Quantity
    {
        get => Source.Quantity;
        set
        {
            if (Source.Quantity == value) return;
            Source.Quantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsQuantityValid));
        }
    }

    public bool IsQuantityValid => Quantity >= 1 && Quantity <= MaxStackSize;

    [ObservableProperty]
    private string _expiresIn = "";

    public void RefreshExpiresIn(DateTimeOffset now)
    {
        var deadline = Timestamp + Ttl;
        var remaining = deadline - now;
        if (remaining <= TimeSpan.Zero) { ExpiresIn = "now"; return; }
        if (remaining.TotalDays >= 1)
            ExpiresIn = $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        else if (remaining.TotalHours >= 1)
            ExpiresIn = $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        else
            ExpiresIn = $"{Math.Max(1, (int)remaining.TotalMinutes)}m";
    }
}

public sealed partial class CalibrationViewModel : ObservableObject
{
    private readonly CalibrationService _calibration;
    private readonly ICommunityCalibrationService? _community;
    private readonly IDialogService? _dialogService;
    private readonly TimeSpan _pendingTtl;
    private readonly DispatcherTimer? _expiryTimer;

    public CalibrationViewModel(
        CalibrationService calibration,
        ICommunityCalibrationService? community = null,
        IDialogService? dialogService = null,
        ArwenSettings? settings = null)
    {
        _calibration = calibration;
        _community = community;
        _dialogService = dialogService;
        _pendingTtl = settings?.PendingObservationTtl ?? TimeSpan.FromHours(24);
        _calibration.DataChanged += (_, _) => Refresh();
        _calibration.PendingChanged += (_, _) => RefreshPending();
        if (_community is not null) _community.FileUpdated += (_, _) => Refresh();
        Refresh();
        RefreshPending();

        // Refresh ExpiresIn strings every 30s. WPF DispatcherTimer requires a
        // Dispatcher; in headless tests Application.Current is null, so skip.
        if (System.Windows.Application.Current?.Dispatcher is { } d)
        {
            _expiryTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(30), DispatcherPriority.Background,
                (_, _) => RefreshExpiryStrings(), d);
            _expiryTimer.Start();
        }
    }

    [ObservableProperty]
    private ObservableCollection<ItemRateRow> _itemRates = [];

    [ObservableProperty]
    private ObservableCollection<SignatureRateRow> _signatureRates = [];

    [ObservableProperty]
    private ObservableCollection<NpcBaselineRow> _npcBaselines = [];

    [ObservableProperty]
    private ObservableCollection<ObservationRow> _observations = [];

    /// <summary>
    /// Subset of <see cref="Observations"/> matching <see cref="ObservationsFilter"/>.
    /// Drives the editable observations tab. When the filter is empty this is a copy of
    /// <see cref="Observations"/>; <see cref="BulkDeleteFilteredCommand"/> operates on
    /// exactly this set so what-you-see is what-gets-deleted.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ObservationRow> _filteredObservations = [];

    /// <summary>Case-insensitive substring filter matched against NpcName and ItemName.</summary>
    [ObservableProperty]
    private string _observationsFilter = "";

    [ObservableProperty]
    private int _flaggedCount;

    [ObservableProperty]
    private int _filteredCount;

    partial void OnObservationsFilterChanged(string value) => RebuildFilteredObservations();

    public ObservableCollection<PendingObservationRow> PendingRows { get; } = [];

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _communitySummary = "";

    [RelayCommand]
    private void Confirm(PendingObservationRow? row)
    {
        if (row is null) return;
        _calibration.ConfirmPending(row.Id, row.Quantity);
    }

    [RelayCommand]
    private void Discard(PendingObservationRow? row)
    {
        if (row is null) return;
        _calibration.DiscardPending(row.Id);
    }

    [RelayCommand]
    private void DeleteObservation(ObservationRow? row)
    {
        if (row is null) return;
        if (_calibration.DeleteObservation(row.ObservationKey))
        {
            StatusMessage = $"Deleted observation: {row.ItemName} → {row.NpcName}";
        }
        else
        {
            StatusMessage = "Observation no longer present — nothing to delete.";
        }
    }

    /// <summary>
    /// Called when the Quantity TextBox loses focus on the editor tab. Pushes the row's
    /// edited Quantity into the canonical observation. On failure (out-of-range, item
    /// missing from reference data, unchanged value), reverts the row to its original
    /// quantity so the UI doesn't display a value that wasn't accepted.
    /// </summary>
    [RelayCommand]
    private void CommitQuantity(ObservationRow? row)
    {
        if (row is null) return;
        if (row.Quantity == row.OriginalQuantity) return;
        if (!row.IsQuantityValid)
        {
            row.Quantity = row.OriginalQuantity;
            StatusMessage = $"Quantity must be between 1 and {row.MaxStackSize}; reverted.";
            return;
        }
        if (_calibration.UpdateObservationQuantity(row.ObservationKey, row.Quantity))
        {
            // Successful update fires DataChanged → Refresh() rebuilds every row from the
            // canonical observation list, so OriginalQuantity on the new row will reflect
            // the change. Set a status message before that rebuild discards this row.
            StatusMessage = $"Updated quantity: {row.ItemName} → {row.NpcName} = {row.Quantity}";
        }
        else
        {
            row.Quantity = row.OriginalQuantity;
            StatusMessage = "Quantity update rejected — observation may have changed; reverted.";
        }
    }

    [RelayCommand]
    private void BulkDeleteFiltered()
    {
        var filter = (ObservationsFilter ?? "").Trim();
        if (filter.Length == 0)
        {
            StatusMessage = "Type a filter first; bulk delete only operates on filtered rows.";
            return;
        }

        var targets = FilteredObservations.ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "No observations match the current filter.";
            return;
        }

        // Confirmation is required for destructive bulk action. When _dialogService is null
        // (test path) we proceed — tests construct the VM directly and assert via the
        // CalibrationService API rather than driving the bulk-delete flow end-to-end.
        if (_dialogService is not null)
        {
            var confirmed = _dialogService.Confirm(
                title: "Delete observations",
                message: $"Delete {targets.Count} observation(s) matching \"{filter}\"?\n\nThis cannot be undone.");
            if (!confirmed) return;
        }

        var keys = new HashSet<string>(targets.Select(r => r.ObservationKey), StringComparer.Ordinal);
        var removed = _calibration.DeleteObservationsByPredicate(
            o => keys.Contains(CalibrationService.ObservationKey(o)));
        StatusMessage = $"Deleted {removed} observation(s) matching \"{filter}\".";
    }

    [RelayCommand]
    private void Share()
    {
        if (_dialogService is null) return;
        var vm = new CommunityShareDialogViewModel(
            moduleDisplayName: "Arwen",
            issueTemplateFile: "arwen-contribution.yml",
            exportJson: note => _calibration.ExportCommunityJson(note));
        _dialogService.ShowDialog(vm, new CommunityShareDialog { DataContext = vm });
    }

    [RelayCommand]
    private async Task RefreshCommunityAsync()
    {
        if (_community is null) return;
        CommunitySummary = "Refreshing…";
        try { await _community.RefreshAsync("arwen"); }
        catch { /* swallow — state reflected in Refresh() */ }
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        var data = _calibration.Data;

        ItemRates = new(data.ItemRates.Values
            .Select(r =>
            {
                var (npc, item) = SplitPipe(r.Keyword);
                return new ItemRateRow
                {
                    NpcName = FormatNpcName(npc),
                    ItemName = item,
                    Rate = r.Rate,
                    SampleCount = r.SampleCount,
                    MinRate = r.MinRate,
                    MaxRate = r.MaxRate,
                };
            })
            .OrderByDescending(r => r.SampleCount)
            .ThenBy(r => r.NpcName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ItemName, StringComparer.OrdinalIgnoreCase));

        SignatureRates = new(data.SignatureRates.Values
            .Select(r =>
            {
                var (npc, sig) = SplitPipe(r.Keyword);
                return new SignatureRateRow
                {
                    NpcName = FormatNpcName(npc),
                    Signature = string.IsNullOrEmpty(sig) ? "(no preferences)" : sig,
                    Rate = r.Rate,
                    SampleCount = r.SampleCount,
                    MinRate = r.MinRate,
                    MaxRate = r.MaxRate,
                };
            })
            .OrderByDescending(r => r.SampleCount)
            .ThenBy(r => r.NpcName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Signature, StringComparer.OrdinalIgnoreCase));

        NpcBaselines = new(data.NpcRates.Values
            .Select(r => new NpcBaselineRow
            {
                NpcName = FormatNpcName(r.Keyword),
                Rate = r.Rate,
                SampleCount = r.SampleCount,
                MinRate = r.MinRate,
                MaxRate = r.MaxRate,
            })
            .OrderByDescending(r => r.SampleCount)
            .ThenBy(r => r.NpcName, StringComparer.OrdinalIgnoreCase));

        var flags = ObservationFlagDetector.Detect(data.Observations);

        Observations = new(data.Observations
            .OrderByDescending(o => o.Timestamp)
            .Select(o =>
            {
                var key = CalibrationService.ObservationKey(o);
                var flag = flags.TryGetValue(key, out var info) ? info : null;
                var row = new ObservationRow
                {
                    ObservationKey = key,
                    NpcName = _calibration.GetNpcDisplayName(o.NpcKey),
                    ItemName = _calibration.GetItemDisplayName(o.ItemInternalName),
                    IconId = _calibration.GetIconId(o.ItemInternalName),
                    Signature = string.IsNullOrEmpty(o.Signature) ? "(none)" : o.Signature,
                    MatchedPreferences = o.MatchedPreferences,
                    ItemValue = o.ItemValue,
                    EffectivePref = o.EffectivePref,
                    FavorDelta = o.FavorDelta,
                    DerivedRate = o.DerivedRate,
                    Timestamp = o.Timestamp,
                    MaxStackSize = _calibration.GetMaxStackSize(o.ItemInternalName),
                    OriginalQuantity = o.Quantity,
                    Flag = flag?.Flag ?? ObservationFlag.None,
                    FlagTooltip = flag is null ? null : BuildFlagTooltip(o.DerivedRate, flag),
                };
                row.Quantity = o.Quantity;
                return row;
            }));

        FlaggedCount = Observations.Count(r => r.IsFlagged);

        RebuildFilteredObservations();

        StatusMessage =
            $"{data.Observations.Count} observation(s) · " +
            $"{data.ItemRates.Count} item rate(s) · " +
            $"{data.SignatureRates.Count} signature rate(s) · " +
            $"{data.NpcRates.Count} NPC baseline(s)" +
            (FlaggedCount > 0 ? $" · {FlaggedCount} flagged" : "");

        UpdateCommunitySummary();
    }

    private void RebuildFilteredObservations()
    {
        var filter = (ObservationsFilter ?? "").Trim();
        if (filter.Length == 0)
        {
            FilteredObservations = new(Observations);
        }
        else
        {
            FilteredObservations = new(Observations.Where(r => MatchesFilter(r, filter)));
        }
        FilteredCount = FilteredObservations.Count;
    }

    private static bool MatchesFilter(ObservationRow row, string filter) =>
        row.NpcName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        row.ItemName.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static string BuildFlagTooltip(double observedRate, ObservationFlagInfo info)
    {
        var direction = observedRate > info.GroupMean ? "above" : "below";
        return $"Rate {observedRate:F4} is {info.SigmasFromMean:F1}σ {direction} mean " +
               $"{info.GroupMean:F4} (group of {info.GroupSampleCount}, σ={info.GroupStdDev:F4}).";
    }

    private void RefreshPending()
    {
        var snapshot = _calibration.PendingObservations;
        var liveIds = new HashSet<Guid>();
        foreach (var p in snapshot) liveIds.Add(p.Id);

        // Drop rows whose entry is no longer pending (confirmed, discarded, evicted).
        for (var i = PendingRows.Count - 1; i >= 0; i--)
        {
            if (!liveIds.Contains(PendingRows[i].Id))
                PendingRows.RemoveAt(i);
        }

        // Append rows for pending entries we don't already have. Existing rows are
        // left alone so an in-progress Quantity edit isn't clobbered by an
        // unrelated PendingChanged tick (e.g. another gift entered the queue).
        var existingIds = PendingRows.Select(r => r.Id).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        foreach (var p in snapshot)
        {
            if (existingIds.Contains(p.Id)) continue;
            var row = new PendingObservationRow
            {
                Source = p,
                NpcName = FormatNpcName(p.NpcKey),
                Ttl = _pendingTtl,
            };
            row.RefreshExpiresIn(now);
            PendingRows.Add(row);
        }

        PendingCount = PendingRows.Count;
    }

    private void RefreshExpiryStrings()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in PendingRows) row.RefreshExpiresIn(now);
    }

    private void UpdateCommunitySummary()
    {
        if (_community is null)
        {
            CommunitySummary = "";
            return;
        }
        var payload = _community.ArwenRates;
        if (payload is null)
        {
            CommunitySummary = "No community data yet.";
            return;
        }
        var snap = _community.GetSnapshot("arwen");
        var total = payload.ItemRates.Count + payload.SignatureRates.Count + payload.NpcRates.Count + payload.KeywordRates.Count;
        var when = snap.FetchedAtUtc is { } t ? $"refreshed {t.LocalDateTime:yyyy-MM-dd HH:mm}" : "no refresh yet";
        CommunitySummary = $"Using {total} community entries · {when}";
    }

    private static (string Left, string Right) SplitPipe(string key)
    {
        var idx = key.IndexOf('|');
        return idx < 0 ? (key, "") : (key[..idx], key[(idx + 1)..]);
    }

    private static string FormatNpcName(string npcKey)
    {
        var name = npcKey.StartsWith("NPC_", StringComparison.Ordinal)
            ? npcKey[4..]
            : npcKey;
        return name.Replace('_', ' ');
    }
}
