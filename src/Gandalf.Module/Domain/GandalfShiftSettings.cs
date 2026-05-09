using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Gandalf.Domain;

/// <summary>
/// Per-shift alarm configuration. Both fields default to "no alarm, no
/// override" — disabled by default and inheriting the global sound when
/// enabled. INPC so the WPF settings view re-renders rows on toggle.
/// </summary>
public sealed class ShiftAlarmConfig : INotifyPropertyChanged
{
    private bool _enabled;
    private string? _soundFilePath;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string? SoundFilePath { get => _soundFilePath; set => Set(ref _soundFilePath, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Map of in-game-time-of-day shift slug → per-shift alarm config. Keyed by
/// the slug from <see cref="Mithril.Shared.Game.TimeOfDayShifts.All"/>.
/// Persisted globally at <c>%LocalAppData%/Mithril/Gandalf/shifts.json</c>;
/// shift transitions are character-agnostic.
/// </summary>
public sealed class GandalfShiftSettings : INotifyPropertyChanged
{
    public Dictionary<string, ShiftAlarmConfig> ByShiftSlug { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Lookup-or-create. Used by the settings view and by
    /// <c>ShiftAlarmService</c> at fire time. Newly-minted entries inherit
    /// the disabled default, so reading a previously-untouched shift does
    /// not silently enable an alarm.
    /// </summary>
    public ShiftAlarmConfig GetOrCreate(string slug)
    {
        if (!ByShiftSlug.TryGetValue(slug, out var config))
        {
            config = new ShiftAlarmConfig();
            ByShiftSlug[slug] = config;
            // Re-fire change so subscribers re-render the row.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ByShiftSlug)));
        }
        return config;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfShiftSettings))]
public partial class GandalfShiftSettingsJsonContext : JsonSerializerContext { }
