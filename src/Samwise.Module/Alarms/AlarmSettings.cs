using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Samwise.State;

namespace Samwise.Alarms;

/// <summary>
/// Per-stage alarm rule. When enabled, a transition into this stage will
/// fire. SoundFilePath overrides the global default; leave blank to use
/// the global SystemSounds.Asterisk fallback.
/// </summary>
public sealed class StageAlarmRule : INotifyPropertyChanged
{
    private bool _enabled;
    private string? _soundFilePath;
    private bool _stopOnInteraction = true;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string? SoundFilePath { get => _soundFilePath; set => Set(ref _soundFilePath, value); }
    public bool StopOnInteraction { get => _stopOnInteraction; set => Set(ref _stopOnInteraction, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public sealed class AlarmSettings : INotifyPropertyChanged
{
    private bool _enabled = true;
    private bool _balloonNotification = true;
    private bool _flashWindow = true;
    private double _snoozeMinutes = 5;
    private double _alarmVolume = 0.8;
    private Dictionary<PlotStage, StageAlarmRule> _rules = DefaultRules();

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool BalloonNotification { get => _balloonNotification; set => Set(ref _balloonNotification, value); }
    public bool FlashWindow { get => _flashWindow; set => Set(ref _flashWindow, value); }
    public double SnoozeMinutes { get => _snoozeMinutes; set => Set(ref _snoozeMinutes, Math.Max(0.5, value)); }
    public double AlarmVolume { get => _alarmVolume; set => Set(ref _alarmVolume, Math.Clamp(value, 0.0, 2.0)); }

    /// <summary>Per-stage alarm rules. Persisted by stage name.</summary>
    public Dictionary<PlotStage, StageAlarmRule> Rules
    {
        get => _rules;
        set
        {
            if (ReferenceEquals(_rules, value)) return;
            DetachRuleEvents(_rules);
            _rules = value ?? DefaultRules();
            AttachRuleEvents(_rules);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rules)));
        }
    }

    public HashSet<string> MutedCrops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> MutedCharacters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AlarmSettings() { AttachRuleEvents(_rules); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private void AttachRuleEvents(Dictionary<PlotStage, StageAlarmRule> rules)
    {
        foreach (var r in rules.Values) r.PropertyChanged += OnRuleChanged;
    }

    private void DetachRuleEvents(Dictionary<PlotStage, StageAlarmRule> rules)
    {
        foreach (var r in rules.Values) r.PropertyChanged -= OnRuleChanged;
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rules)));

    private static Dictionary<PlotStage, StageAlarmRule> DefaultRules() => new()
    {
        [PlotStage.Ripe]            = new() { Enabled = true },
        [PlotStage.Thirsty]         = new() { Enabled = false },
        [PlotStage.NeedsFertilizer] = new() { Enabled = false },
    };
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

    private double _harvestedAutoClearMinutes = 10;
    /// <summary>
    /// How long a harvested plot card lingers before being auto-removed.
    /// Defaults to 10 minutes — plants grow in seconds to a few minutes,
    /// so a long retention window just clutters the dashboard.
    /// </summary>
    public double HarvestedAutoClearMinutes
    {
        get => _harvestedAutoClearMinutes;
        set
        {
            var clamped = Math.Clamp(value, 0.5, 24 * 60);
            if (Math.Abs(_harvestedAutoClearMinutes - clamped) < 1e-6) return;
            _harvestedAutoClearMinutes = clamped;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HarvestedAutoClearMinutes)));
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
