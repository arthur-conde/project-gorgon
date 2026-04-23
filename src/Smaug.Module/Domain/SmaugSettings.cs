using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Gorgon.Shared.Reference;

namespace Smaug.Domain;

public sealed class SmaugSettings : INotifyPropertyChanged
{
    private CalibrationSettings _calibration = new();

    /// <summary>Merge mode + auto-refresh toggle for community-aggregated vendor price rates.</summary>
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

    public SmaugSettings()
    {
        _calibration.PropertyChanged += OnCalibrationChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnCalibrationChanged(object? sender, PropertyChangedEventArgs e)
        => OnChanged(nameof(Calibration));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SmaugSettings))]
[JsonSerializable(typeof(CalibrationSettings))]
public partial class SmaugJsonContext : JsonSerializerContext;
