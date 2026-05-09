using System.Text.Json.Serialization;
using Mithril.Shared.Settings;

namespace Gandalf.Domain;

/// <summary>
/// Per-shift alarm configuration. Both fields default to "no alarm, no
/// override" — disabled by default and inheriting the global sound when
/// enabled. Inherits <see cref="SettingsNode"/> so toggle mutations bubble
/// through the parent <see cref="GandalfShiftSettings"/> to the autosaver
/// at the root.
/// </summary>
public sealed class ShiftAlarmConfig : SettingsNode
{
    private bool _enabled;
    private string? _soundFilePath;

    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public string? SoundFilePath { get => _soundFilePath; set => Set(ref _soundFilePath, value); }
}

/// <summary>
/// Map of in-game-time-of-day shift slug → per-shift alarm config. Keyed by
/// the slug from <see cref="Mithril.Shared.Game.IShiftCatalog.Shifts"/>;
/// slugs are the persistence contract — preserve them across catalog
/// schema versions so user-toggled rows don't orphan on update. Persisted
/// globally at <c>%LocalAppData%/Mithril/Gandalf/shifts.json</c>; shift
/// transitions are character-agnostic.
///
/// <para>Implements <see cref="IPostLoadInit"/> because STJ source-gen
/// populates <see cref="ByShiftSlug"/> without going through
/// <see cref="GetOrCreate"/> — without re-wiring bubbling on the
/// freshly-loaded children, toggling an existing shift's <c>Enabled</c>
/// would fire on the child only and the autosaver would never see it
/// (the regression that motivated <see cref="SettingsNode"/>).</para>
/// </summary>
public sealed class GandalfShiftSettings : SettingsNode, IPostLoadInit
{
    public Dictionary<string, ShiftAlarmConfig> ByShiftSlug { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Lookup-or-create. Used by the settings view and by
    /// <c>ShiftAlarmService</c> at fire time. Newly-minted entries inherit
    /// the disabled default, so reading a previously-untouched shift does
    /// not silently enable an alarm. Bubbling is wired on the new child so
    /// later <c>Enabled</c> / <c>SoundFilePath</c> mutations propagate.
    /// </summary>
    public ShiftAlarmConfig GetOrCreate(string slug)
    {
        if (!ByShiftSlug.TryGetValue(slug, out var config))
        {
            config = new ShiftAlarmConfig();
            ByShiftSlug[slug] = config;
            Bubble(config);
            // Re-fire so the settings view re-renders the row list.
            RaisePropertyChanged(nameof(ByShiftSlug));
        }
        return config;
    }

    public void PostLoadInit()
    {
        // Idempotent — Unbubble is a no-op for unsubscribed children, so a
        // double-PostLoadInit (tests, hot-reload) won't double-wire.
        foreach (var child in ByShiftSlug.Values)
        {
            Unbubble(child);
            Bubble(child);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GandalfShiftSettings))]
public partial class GandalfShiftSettingsJsonContext : JsonSerializerContext { }
