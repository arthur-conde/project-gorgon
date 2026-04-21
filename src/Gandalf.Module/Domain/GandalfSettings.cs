using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Gandalf.Domain;

public sealed class GandalfSettings : INotifyPropertyChanged
{
    private bool _alarmEnabled = true;
    private bool _flashWindow = true;
    private double _alarmVolume = 0.8;
    private string? _soundFilePath;
    private double _snoozeMinutes = 5;

    public bool AlarmEnabled { get => _alarmEnabled; set => Set(ref _alarmEnabled, value); }
    public bool FlashWindow { get => _flashWindow; set => Set(ref _flashWindow, value); }
    public double AlarmVolume { get => _alarmVolume; set => Set(ref _alarmVolume, Math.Clamp(value, 0.0, 2.0)); }
    public string? SoundFilePath { get => _soundFilePath; set => Set(ref _soundFilePath, value); }
    public double SnoozeMinutes { get => _snoozeMinutes; set => Set(ref _snoozeMinutes, Math.Max(0.5, value)); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfSettings))]
public partial class GandalfSettingsJsonContext : JsonSerializerContext { }
