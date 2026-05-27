using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Game;

/// <summary>
/// One named portion of the in-game day. <see cref="StartHour"/> is the
/// in-game hour at which the shift begins; the shift runs until the next
/// shift's start hour.
/// </summary>
public sealed record ShiftDefinition(string Slug, string Label, string Emoji, int StartHour);

/// <summary>
/// Project Gorgon's published in-game-time-of-day shifts (Midnight / Dawn /
/// Morning / Afternoon / Dusk / Night). Backed by a bundled JSON catalog so
/// the schedule is data, not code; consumers receive an
/// <see cref="IShiftCatalog"/> instance via DI and read
/// <see cref="Shifts"/> directly.
///
/// <para>Snapshot source: <c>https://pgemissary.com/static/js/game_clock.js</c> —
/// the <c>TIME_OF_DAY</c> constant. Same trust model as the in-game clock
/// anchor in <see cref="GameClock"/>; pgemissary publishes the table only as
/// a JS constant on a page that loads dynamically, so there's no API surface
/// to fetch it from at runtime. The bundled JSON is the source of truth and
/// gets re-snapshotted in coordination with whatever Gorgon release prompts
/// a pgemissary update.</para>
/// </summary>
public interface IShiftCatalog
{
    /// <summary>The published shifts, in <see cref="ShiftDefinition.StartHour"/> order.</summary>
    IReadOnlyList<ShiftDefinition> Shifts { get; }

    /// <summary>
    /// The earliest real-time instant ≥ <paramref name="floor"/> at which any
    /// shift's <c>StartHour</c> is reached, plus the shift definition that
    /// begins at that moment. Built on
    /// <see cref="IGameClock.NextOccurrence"/>: each shift's transition is
    /// "the next real-time moment the in-game clock reads <c>StartHour:00</c>",
    /// and we return the soonest across all shifts in the catalog.
    /// </summary>
    (DateTimeOffset At, ShiftDefinition Shift) NextTransition(IGameClock clock, DateTimeOffset floor);
}

/// <summary>
/// JSON shape for the bundled <c>shifts.json</c> catalog. Versioned via
/// <see cref="SchemaVersion"/>; loaders reject payloads whose version
/// they don't understand, falling back to the last known catalog (or to
/// <see cref="JsonShiftCatalog.HardcodedFallback"/> on first run).
/// </summary>
public sealed class ShiftCatalogPayload
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("snapshotDate")]
    public string? SnapshotDate { get; set; }

    [JsonPropertyName("shifts")]
    public List<ShiftEntry> Shifts { get; set; } = new();

    public sealed class ShiftEntry
    {
        [JsonPropertyName("slug")] public string Slug { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("emoji")] public string Emoji { get; set; } = "";
        [JsonPropertyName("startHour")] public int StartHour { get; set; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ShiftCatalogPayload))]
public partial class ShiftCatalogJsonContext : JsonSerializerContext { }

/// <summary>
/// Loads the shift catalog from a bundled JSON file (<c>shifts.json</c>
/// alongside the other CDN-mirrored bundled data) and exposes it as an
/// <see cref="IShiftCatalog"/>. Falls back to a hardcoded snapshot if the
/// file is missing, malformed, or carries an unsupported schema version —
/// the catalog is critical-path for the shell countdown chip and the
/// Gandalf shift alarms, so we'd rather show stale data than nothing.
/// </summary>
public sealed class JsonShiftCatalog : IShiftCatalog
{
    /// <summary>Latest schema version this loader understands.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Hardcoded shift table. Used when the bundled file fails to load —
    /// matches the snapshot in <c>shifts.json</c> as of <c>2026-05-09</c>.
    /// Kept identical so the failure mode is "stale by a Gorgon patch,"
    /// never "no shifts at all."
    /// </summary>
    public static readonly IReadOnlyList<ShiftDefinition> HardcodedFallback =
    [
        new("midnight",  "Midnight",  "\U0001F311",            0),
        new("dawn",      "Dawn",      "\U0001F305",            5),
        new("morning",   "Morning",   "☀️",          8),
        new("afternoon", "Afternoon", "\U0001F324️",     12),
        new("dusk",      "Dusk",      "\U0001F307",           17),
        new("night",     "Night",     "\U0001F319",           20),
    ];

    public IReadOnlyList<ShiftDefinition> Shifts { get; }

    public JsonShiftCatalog(string? bundledDir = null, ILogger? logger = null)
    {
        var dir = bundledDir ?? Path.Combine(AppContext.BaseDirectory, "Reference", "BundledData");
        var path = Path.Combine(dir, "shifts.json");
        Shifts = TryLoad(path, logger) ?? HardcodedFallback;
    }

    private JsonShiftCatalog(IReadOnlyList<ShiftDefinition> shifts)
    {
        Shifts = shifts;
    }

    /// <summary>Test-only construction from an in-memory list.</summary>
    public static IShiftCatalog FromShifts(IReadOnlyList<ShiftDefinition> shifts) =>
        new JsonShiftCatalog(shifts);

    /// <summary>
    /// Test-only loader that reads a specific JSON file. Returns null if the
    /// file is malformed or carries an unsupported schema version.
    /// </summary>
    public static IReadOnlyList<ShiftDefinition>? TryLoadFromFile(string path, ILogger? logger = null) =>
        TryLoad(path, logger);

    public (DateTimeOffset At, ShiftDefinition Shift) NextTransition(IGameClock clock, DateTimeOffset floor)
    {
        DateTimeOffset? soonestAt = null;
        ShiftDefinition? soonestShift = null;
        foreach (var shift in Shifts)
        {
            var at = clock.NextOccurrence(new GameTimeOfDay(shift.StartHour, 0), floor);
            if (soonestAt is null || at < soonestAt)
            {
                soonestAt = at;
                soonestShift = shift;
            }
        }
        if (soonestShift is null)
            throw new InvalidOperationException("Shift catalog is empty — cannot compute next transition.");
        return (soonestAt!.Value, soonestShift);
    }

    /// <summary>
    /// Format a real-time remaining duration as "Mm SSs" under one hour and
    /// "Hh MMm" at one hour or more. Used by the shell countdown chip
    /// (<c>"Next: Dawn in 4m 23s"</c>) — kept stable-width across the whole
    /// PG day (max ~5 in-game hours = 25 real minutes between transitions,
    /// so the "Hh MMm" branch is rarely reached but accommodates a
    /// future shift schedule with sparser transitions).
    /// </summary>
    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        if (remaining < TimeSpan.FromHours(1))
        {
            var totalSeconds = (int)Math.Floor(remaining.TotalSeconds);
            var minutes = totalSeconds / 60;
            var seconds = totalSeconds % 60;
            return $"{minutes}m {seconds:D2}s";
        }
        var hours = (int)Math.Floor(remaining.TotalHours);
        var minutesPart = (int)Math.Floor(remaining.TotalMinutes) - hours * 60;
        return $"{hours}h {minutesPart:D2}m";
    }

    private static IReadOnlyList<ShiftDefinition>? TryLoad(string path, ILogger? logger)
    {
        if (!File.Exists(path))
        {
            logger?.LogDiagnosticWarn("ShiftCatalog", $"shifts.json missing at {path}; using hardcoded fallback.");
            return null;
        }

        ShiftCatalogPayload? payload;
        try
        {
            using var stream = File.OpenRead(path);
            payload = JsonSerializer.Deserialize(stream, ShiftCatalogJsonContext.Default.ShiftCatalogPayload);
        }
        catch (Exception ex)
        {
            logger?.LogDiagnosticWarn("ShiftCatalog", $"shifts.json parse failed ({ex.Message}); using hardcoded fallback.");
            return null;
        }

        if (payload is null)
        {
            logger?.LogDiagnosticWarn("ShiftCatalog", "shifts.json deserialized to null; using hardcoded fallback.");
            return null;
        }

        if (payload.SchemaVersion != CurrentSchemaVersion)
        {
            logger?.LogDiagnosticWarn("ShiftCatalog",
                $"shifts.json schemaVersion {payload.SchemaVersion} != expected {CurrentSchemaVersion}; using hardcoded fallback.");
            return null;
        }

        if (payload.Shifts is null || payload.Shifts.Count == 0)
        {
            logger?.LogDiagnosticWarn("ShiftCatalog", "shifts.json has no shifts; using hardcoded fallback.");
            return null;
        }

        var shifts = new List<ShiftDefinition>(payload.Shifts.Count);
        foreach (var entry in payload.Shifts)
        {
            if (string.IsNullOrEmpty(entry.Slug) || string.IsNullOrEmpty(entry.Label))
            {
                logger?.LogDiagnosticWarn("ShiftCatalog",
                    $"shifts.json entry has empty slug/label (slug='{entry.Slug}', label='{entry.Label}'); using hardcoded fallback.");
                return null;
            }
            if (entry.StartHour is < 0 or > 23)
            {
                logger?.LogDiagnosticWarn("ShiftCatalog",
                    $"shifts.json entry '{entry.Slug}' has out-of-range StartHour {entry.StartHour}; using hardcoded fallback.");
                return null;
            }
            shifts.Add(new ShiftDefinition(entry.Slug, entry.Label, entry.Emoji ?? "", entry.StartHour));
        }

        // Sort defensively — consumers (settings rows, countdown chip) rely
        // on StartHour order. The bundled file ships sorted; this protects
        // a future hand-edit from breaking the UI silently.
        shifts.Sort((a, b) => a.StartHour.CompareTo(b.StartHour));
        return shifts;
    }
}
