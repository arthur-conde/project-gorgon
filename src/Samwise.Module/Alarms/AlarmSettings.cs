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
    private bool _sessionActive;
    public bool SessionActive { get => _sessionActive; set { if (_sessionActive == value) return; _sessionActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SessionActive))); } }

    public AlarmSettings Alarms { get; set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SamwiseSettings))]
public partial class SamwiseSettingsJsonContext : JsonSerializerContext { }
