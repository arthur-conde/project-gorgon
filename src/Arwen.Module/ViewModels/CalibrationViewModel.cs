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

/// <summary>Row for the raw observations grid.</summary>
public sealed class ObservationRow
{
    public required string NpcName { get; init; }
    public required string ItemName { get; init; }
    public required int Quantity { get; init; }
    public required string Signature { get; init; }
    public required double ItemValue { get; init; }
    public required double EffectivePref { get; init; }
    public required double FavorDelta { get; init; }
    public required double DerivedRate { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
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

        Observations = new(data.Observations
            .OrderByDescending(o => o.Timestamp)
            .Select(o => new ObservationRow
            {
                NpcName = FormatNpcName(o.NpcKey),
                ItemName = o.ItemInternalName,
                Quantity = o.Quantity,
                Signature = string.IsNullOrEmpty(o.Signature) ? "(none)" : o.Signature,
                ItemValue = o.ItemValue,
                EffectivePref = o.EffectivePref,
                FavorDelta = o.FavorDelta,
                DerivedRate = o.DerivedRate,
                Timestamp = o.Timestamp,
            }));

        StatusMessage =
            $"{data.Observations.Count} observation(s) · " +
            $"{data.ItemRates.Count} item rate(s) · " +
            $"{data.SignatureRates.Count} signature rate(s) · " +
            $"{data.NpcRates.Count} NPC baseline(s)";

        UpdateCommunitySummary();
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
