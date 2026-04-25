using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Mithril.Shared.Reference;

namespace Arwen.Domain;

/// <summary>
/// Global user preferences for Arwen — currently just the community-calibration settings.
/// Per-character favor state lives in <c>ArwenFavorState</c> (per-character store).
/// </summary>
public sealed class ArwenSettings : INotifyPropertyChanged
{
    private CalibrationSettings _calibration = new();
    /// <summary>Merge mode + auto-refresh toggle for the community-aggregated gift rates.</summary>
    public CalibrationSettings Calibration
    {
        get => _calibration;
        set
        {
            if (ReferenceEquals(_calibration, value)) return;
            _calibration.PropertyChanged -= OnCalibrationChanged;
            _calibration = value;
            _calibration.PropertyChanged += OnCalibrationChanged;
            OnChanged(nameof(Calibration));
        }
    }

    public ArwenSettings()
    {
        _calibration.PropertyChanged += OnCalibrationChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnCalibrationChanged(object? sender, PropertyChangedEventArgs e)
        => OnChanged(nameof(Calibration));
}

public sealed class NpcFavorSnapshot
{
    public double ExactFavor { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ArwenSettings))]
[JsonSerializable(typeof(CalibrationSettings))]
public partial class ArwenJsonContext : JsonSerializerContext;
