using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Mithril.Shared.Settings;

/// <summary>
/// Cross-cutting display + behavior preferences that span more than one
/// module. Lives in <c>Mithril.Shared</c> (not the shell) so module-level
/// consumers — Gandalf's TimerDialog picker, ShiftAlarmRow display strings,
/// shell's in-game-clock label, etc. — can read it without taking a
/// dependency on <c>Mithril.Shell</c>.
///
/// <para>Persisted to <c>%LocalAppData%/Mithril/preferences.json</c>. Loaded
/// via <see cref="DependencyInjection.ServiceCollectionExtensions.AddMithrilSettings{T}"/>.</para>
/// </summary>
public sealed class UserPreferences : INotifyPropertyChanged
{
    private bool _use24HourClock;

    /// <summary>
    /// When <c>true</c>, in-game time is rendered as <c>HH:mm</c> (24-hour);
    /// otherwise <c>h:mm AM/PM</c>. Defaults to <c>false</c> so existing
    /// users see the same display they had before this preference shipped.
    /// </summary>
    public bool Use24HourClock
    {
        get => _use24HourClock;
        set => Set(ref _use24HourClock, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UserPreferences))]
public partial class UserPreferencesJsonContext : JsonSerializerContext { }
