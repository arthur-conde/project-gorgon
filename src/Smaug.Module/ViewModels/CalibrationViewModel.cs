using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mithril.Shared.Reference;
using Smaug.Domain;

namespace Smaug.ViewModels;

public sealed class ObservationRow
{
    public required string NpcName { get; init; }
    public required string ItemName { get; init; }
    public int IconId { get; init; }
    public required string FavorTier { get; init; }
    public required int CivicPride { get; init; }
    public required decimal BaseValue { get; init; }
    public required long PricePaid { get; init; }
    public required double Ratio { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed partial class CalibrationViewModel : ObservableObject
{
    private readonly PriceCalibrationService _calibration;
    private readonly ICommunityCalibrationService? _community;
    private readonly IReferenceDataService _refData;

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _communitySummary = "";

    public ObservableCollection<ObservationRow> Observations { get; } = new();

    public CalibrationViewModel(
        PriceCalibrationService calibration,
        IReferenceDataService refData,
        ICommunityCalibrationService? community = null)
    {
        _calibration = calibration;
        _refData = refData;
        _community = community;

        _calibration.DataChanged += (_, _) => Refresh();
        if (_community is not null) _community.FileUpdated += (_, key) =>
        {
            if (key == "smaug") Refresh();
        };

        Refresh();
    }

    private void Refresh()
    {
        Observations.Clear();
        foreach (var obs in _calibration.Data.Observations.OrderByDescending(o => o.Timestamp).Take(500))
        {
            _refData.Npcs.TryGetValue(obs.NpcKey, out var npc);
            _refData.ItemsByInternalName.TryGetValue(obs.InternalName, out var item);
            Observations.Add(new ObservationRow
            {
                NpcName = npc?.Name ?? obs.NpcKey.Replace("NPC_", ""),
                ItemName = item?.Name ?? obs.InternalName,
                IconId = item?.IconId ?? 0,
                FavorTier = obs.FavorTier,
                CivicPride = obs.CivicPrideLevel,
                BaseValue = obs.BaseValue,
                PricePaid = obs.PricePaid,
                Ratio = obs.Ratio,
                Timestamp = obs.Timestamp,
            });
        }

        StatusMessage = _calibration.Data.Observations.Count == 0
            ? "No observations yet."
            : $"{_calibration.Data.Observations.Count:N0} total observations.";

        var rates = _community?.SmaugRates;
        CommunitySummary = rates is null
            ? "Community data: not loaded."
            : $"Community data: {rates.AbsoluteRates.Count:N0} absolute / {rates.RatioRates.Count:N0} ratio rates.";
    }

    [RelayCommand]
    private Task RefreshCommunity()
    {
        if (_community is null) return Task.CompletedTask;
        return _community.RefreshAsync("smaug");
    }
}
