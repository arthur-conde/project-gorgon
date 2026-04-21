using System.Collections.ObjectModel;
using Arwen.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Arwen.ViewModels;

/// <summary>Row for the category rates summary grid.</summary>
public sealed class CategoryRateRow
{
    public required string Keyword { get; init; }
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
    public required string Keyword { get; init; }
    public required double ItemValue { get; init; }
    public required double Pref { get; init; }
    public required double FavorDelta { get; init; }
    public required double DerivedRate { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}

public sealed partial class CalibrationViewModel : ObservableObject
{
    private readonly CalibrationService _calibration;

    public CalibrationViewModel(CalibrationService calibration)
    {
        _calibration = calibration;
        _calibration.DataChanged += (_, _) => Refresh();
        Refresh();
    }

    [ObservableProperty]
    private ObservableCollection<CategoryRateRow> _rates = [];

    [ObservableProperty]
    private ObservableCollection<ObservationRow> _observations = [];

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private string _keywordFilter = "";

    partial void OnKeywordFilterChanged(string value) => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var data = _calibration.Data;

        // Rates
        var rates = data.Rates.Values
            .OrderByDescending(r => r.SampleCount)
            .ThenBy(r => r.Keyword, StringComparer.OrdinalIgnoreCase)
            .Select(r => new CategoryRateRow
            {
                Keyword = r.Keyword,
                Rate = r.Rate,
                SampleCount = r.SampleCount,
                MinRate = r.MinRate,
                MaxRate = r.MaxRate,
            })
            .ToList();
        Rates = new ObservableCollection<CategoryRateRow>(rates);

        // Observations — apply keyword filter
        var filter = KeywordFilter;
        var observations = data.Observations
            .Where(o => string.IsNullOrWhiteSpace(filter)
                || o.MatchedKeyword.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || o.NpcKey.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || o.ItemInternalName.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.Timestamp)
            .Select(o => new ObservationRow
            {
                NpcName = FormatNpcName(o.NpcKey),
                ItemName = o.ItemInternalName,
                Keyword = o.MatchedKeyword,
                ItemValue = o.ItemValue,
                Pref = o.Pref,
                FavorDelta = o.FavorDelta,
                DerivedRate = o.DerivedRate,
                Timestamp = o.Timestamp,
            })
            .ToList();
        Observations = new ObservableCollection<ObservationRow>(observations);

        StatusMessage = $"{data.Rates.Count} keyword(s) calibrated from {data.Observations.Count} observation(s)";
    }

    private static string FormatNpcName(string npcKey)
    {
        // "NPC_Sanja" → "Sanja", "NPC_Sir_Coth" → "Sir Coth"
        var name = npcKey.StartsWith("NPC_", StringComparison.Ordinal)
            ? npcKey[4..]
            : npcKey;
        return name.Replace('_', ' ');
    }
}
