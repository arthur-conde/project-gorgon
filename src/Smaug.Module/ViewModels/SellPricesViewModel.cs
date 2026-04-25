using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Mithril.Shared.Reference;
using Smaug.Domain;

namespace Smaug.ViewModels;

public sealed class SellPricesRow
{
    public required string NpcName { get; init; }
    public required string Item { get; init; }
    public int IconId { get; init; }
    public required string FavorTier { get; init; }
    public required string CivicPride { get; init; }
    public required double ExpectedPrice { get; init; }
    public required long MinPrice { get; init; }
    public required long MaxPrice { get; init; }
    public required int LocalSamples { get; init; }
    public required int CommunitySamples { get; init; }
    public string Kind { get; init; } = ""; // "Absolute" or "Ratio"
}

public sealed partial class SellPricesViewModel : ObservableObject
{
    private readonly PriceCalibrationService _calibration;
    private readonly ICommunityCalibrationService? _community;
    private readonly IReferenceDataService _refData;

    [ObservableProperty] private string _statusMessage = "";

    public ObservableCollection<SellPricesRow> Rows { get; } = new();

    public SellPricesViewModel(
        PriceCalibrationService calibration,
        IReferenceDataService refData,
        ICommunityCalibrationService? community = null)
    {
        _calibration = calibration;
        _refData = refData;
        _community = community;

        _calibration.DataChanged += (_, _) => Refresh();
        if (_community is not null) _community.FileUpdated += (_, _) => Refresh();

        Refresh();
    }

    private void Refresh()
    {
        Rows.Clear();

        var communityAbs = _community?.SmaugRates?.AbsoluteRates;
        var communityRatio = _community?.SmaugRates?.RatioRates;

        foreach (var (key, rate) in _calibration.EffectiveAbsoluteRates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var parts = key.Split('|', 4);
            if (parts.Length < 4) continue;
            var (npc, item, tier, cp) = (parts[0], parts[1], parts[2], parts[3]);
            var localSamples = _calibration.Data.AbsoluteRates.TryGetValue(key, out var lr) ? lr.SampleCount : 0;
            var communitySamples = communityAbs?.TryGetValue(key, out var cr) == true ? cr!.SampleCount : 0;

            _refData.Npcs.TryGetValue(npc, out var npcEntry);
            _refData.ItemsByInternalName.TryGetValue(item, out var itemEntry);

            Rows.Add(new SellPricesRow
            {
                NpcName = npcEntry?.Name ?? npc.Replace("NPC_", ""),
                Item = itemEntry?.Name ?? item,
                IconId = itemEntry?.IconId ?? 0,
                FavorTier = tier,
                CivicPride = cp,
                ExpectedPrice = rate.AvgPrice,
                MinPrice = rate.MinPrice,
                MaxPrice = rate.MaxPrice,
                LocalSamples = localSamples,
                CommunitySamples = communitySamples,
                Kind = "Absolute",
            });
        }

        foreach (var (key, rate) in _calibration.EffectiveRatioRates.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var parts = key.Split('|', 4);
            if (parts.Length < 4) continue;
            var (npc, bucket, tier, cp) = (parts[0], parts[1], parts[2], parts[3]);
            var localSamples = _calibration.Data.RatioRates.TryGetValue(key, out var lr) ? lr.SampleCount : 0;
            var communitySamples = communityRatio?.TryGetValue(key, out var cr) == true ? cr!.SampleCount : 0;

            _refData.Npcs.TryGetValue(npc, out var npcEntry);

            Rows.Add(new SellPricesRow
            {
                NpcName = npcEntry?.Name ?? npc.Replace("NPC_", ""),
                Item = $"[{bucket}] (any)",
                FavorTier = tier,
                CivicPride = cp,
                ExpectedPrice = rate.AvgRatio,      // a multiplier; presented without units in v1
                MinPrice = (long)Math.Round(rate.MinRatio * 100),
                MaxPrice = (long)Math.Round(rate.MaxRatio * 100),
                LocalSamples = localSamples,
                CommunitySamples = communitySamples,
                Kind = "Ratio",
            });
        }

        StatusMessage = Rows.Count == 0
            ? "No calibration data yet — sell items to vendors and observations will appear here."
            : $"{Rows.Count:N0} rate rows ({_calibration.EffectiveAbsoluteRates.Count} absolute, {_calibration.EffectiveRatioRates.Count} ratio).";
    }
}
