using System.Text.Json.Serialization;

namespace Gandalf.Domain;

/// <summary>
/// What kind of trigger fires this timer. <see cref="Countdown"/> uses
/// <see cref="GandalfTimerDef.Duration"/>; <see cref="GameTimeOfDay"/> uses
/// <see cref="GandalfTimerDef.GameHour"/>/<see cref="GandalfTimerDef.GameMinute"/>
/// (Project Gorgon in-game wall-clock time, 24h).
/// </summary>
public enum GandalfTriggerKind
{
    Countdown = 0,
    GameTimeOfDay = 1,
}

/// <summary>
/// Immutable shape of a timer definition — what the user configured, shared across every character.
/// Lives in the global <see cref="GandalfDefinitions"/> blob. Progress for this timer is tracked
/// per-character in <see cref="GandalfProgress"/> keyed by <see cref="Id"/>.
/// </summary>
public sealed class GandalfTimerDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public GandalfTriggerKind Kind { get; set; } = GandalfTriggerKind.Countdown;
    public TimeSpan Duration { get; set; }
    public int? GameHour { get; set; }
    public int? GameMinute { get; set; }
    public bool Recurring { get; set; }

    /// <summary>
    /// Free-form area label as displayed/typed by the user — e.g. "Serbule",
    /// "Hogan's Keep", "My Hideout". Used as the dashboard group header.
    /// </summary>
    public string Area { get; set; } = "";

    /// <summary>
    /// Canonical PG area key (e.g. <c>"AreaSerbule"</c>) when <see cref="Area"/>
    /// resolves to a known <c>areas.json</c> entry by FriendlyName. <c>null</c>
    /// for free-form values that don't match the reference data. Side-channel —
    /// the user never sees this, but it lets future consumers cross-reference
    /// PG area metadata without re-doing the lookup.
    /// </summary>
    public string? AreaKey { get; set; }

    /// <summary>
    /// Per-timer alarm sound path. <c>null</c> falls back to the global default
    /// in <see cref="GandalfSettings.SoundFilePath"/>. Volume + flash-window
    /// stay global by design — per-timer customization there is over-scope.
    /// </summary>
    public string? SoundFilePath { get; set; }

    [JsonIgnore]
    public string GroupKey => Area;
}
