namespace Gandalf.Domain;

/// <summary>
/// Read-only feed of timer rows, common to every Gandalf source — user-curated,
/// quest cooldowns, loot cooldowns. The catalog is the universe of timers a source
/// knows about; the progress map is the per-character runtime state for whichever
/// of those timers the player has interacted with. The split mirrors the natural
/// separation of derived sources: catalogs are computed from CDN/calibration data
/// (shared global), progress is observed from logs (per-character).
///
/// Forward-compat: <see cref="TimerReady"/> is the canonical signal a row finished
/// cooling. <see cref="Services.TimerAlarmService"/> consumes it today; a future
/// shell-level inbox / notification center will subscribe to the same surface to
/// fan in ready-but-unacked rows across every module. Sources must NOT fire alarms
/// directly — they emit, services consume.
/// </summary>
public interface ITimerSource
{
    /// <summary>
    /// Stable wire identifier. Strings are used (not enums) because future external
    /// consumers — a shell inbox, an export format — index by this tuple alongside
    /// the row key.
    /// </summary>
    string SourceId { get; }

    IReadOnlyList<TimerCatalogEntry> Catalog { get; }

    /// <summary>
    /// Per-row runtime state, keyed by <see cref="TimerCatalogEntry.Key"/>. A row
    /// missing from this map is idle (catalog-known, never started). A row whose
    /// <see cref="TimerProgressEntry.DismissedAt"/> is non-null is "active but
    /// hidden" — keep the row, don't delete it; the next observation resurrects
    /// it with a fresh cooldown.
    /// </summary>
    IReadOnlyDictionary<string, TimerProgressEntry> Progress { get; }

    event EventHandler? CatalogChanged;
    event EventHandler? ProgressChanged;
    event EventHandler<TimerReadyEventArgs>? TimerReady;
}

public sealed record TimerCatalogEntry(
    string Key,
    string DisplayName,
    string? Region,
    TimeSpan Duration,
    object? SourceMetadata);

public sealed record TimerProgressEntry(
    string Key,
    DateTimeOffset StartedAt,
    DateTimeOffset? DismissedAt);

public sealed class TimerReadyEventArgs : EventArgs
{
    public required string SourceId { get; init; }
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset ReadyAt { get; init; }
    public object? SourceMetadata { get; init; }
}
