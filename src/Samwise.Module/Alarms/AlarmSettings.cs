using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Samwise.Alarms;

public sealed class AlarmSettings : INotifyPropertyChanged
{
    private bool _enabled = true;
    private string? _soundFilePath;
    private bool _balloonNotification = true;
    private bool _flashWindow = true;
    private double _snoozeMinutes = 5;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string? SoundFilePath { get => _soundFilePath; set => Set(ref _soundFilePath, value); }
    public bool BalloonNotification { get => _balloonNotification; set => Set(ref _balloonNotification, value); }
    public bool FlashWindow { get => _flashWindow; set => Set(ref _flashWindow, value); }
    public double SnoozeMinutes { get => _snoozeMinutes; set => Set(ref _snoozeMinutes, Math.Max(0.5, value)); }
    public HashSet<string> MutedCrops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MutedCharacters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class SamwiseSettings : INotifyPropertyChanged
{
    private AlarmSettings _alarms = new();
    public AlarmSettings Alarms
    {
        get => _alarms;
        set
        {
            if (ReferenceEquals(_alarms, value)) return;
            _alarms.PropertyChanged -= OnAlarmsChanged;
            _alarms = value;
            _alarms.PropertyChanged += OnAlarmsChanged;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Alarms)));
        }
    }

    public SamwiseSettings() { _alarms.PropertyChanged += OnAlarmsChanged; }

    private void OnAlarmsChanged(object? sender, PropertyChangedEventArgs e)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Alarms)));

    public event PropertyChangedEventHandler? PropertyChanged;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SamwiseSettings))]
public partial class SamwiseSettingsJsonContext : JsonSerializerContext { }
