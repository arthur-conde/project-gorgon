using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    /// Per-row progress lookup. Returns <c>true</c> and yields the entry when a row
    /// has been started (even if dismissed); returns <c>false</c> for rows in
    /// catalog-but-never-started state. Avoids the
    /// <see cref="Progress"/> snapshot-dictionary allocation for callers that only
    /// care about a single key (e.g. binders applying a per-key delta).
    /// </summary>
    bool TryGetProgress(string key, [NotNullWhen(true)] out TimerProgressEntry? progress);

    event EventHandler<TimerReadyEventArgs>? TimerReady;

    /// <summary>
    /// Per-key batched change feed. Sources coalesce many simultaneous mutations
    /// (calibration overlay refresh, journal load, character switch) into a single
    /// event invocation with N deltas — one event per logical mutation, not N —
    /// so consumers can apply all changes and call
    /// <c>ICollectionView.Refresh</c> at most once per batch.
    /// </summary>
    event EventHandler<TimerRowsChangedEventArgs>? RowsChanged;
}

public sealed record TimerCatalogEntry(
    string Key,
    string DisplayName,
    string? Region,
    TimeSpan Duration,
    object? SourceMetadata);

/// <summary>
/// <para><see cref="FiringAt"/> is an optional per-row firing instant — used by
/// the User feed for game-clock alarms whose firing moment isn't <c>StartedAt +
/// Catalog.Duration</c>. Derived sources (Quest, Loot, Dashboard) leave it null
/// and consumers fall back to that default arithmetic, so their behavior is
/// unchanged.</para>
/// </summary>
public sealed record TimerProgressEntry(
    string Key,
    DateTimeOffset StartedAt,
    DateTimeOffset? DismissedAt,
    DateTimeOffset? FiringAt = null);

public sealed class TimerReadyEventArgs : EventArgs
{
    public required string SourceId { get; init; }
    public required string Key { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset ReadyAt { get; init; }
    public object? SourceMetadata { get; init; }
}

/// <summary>
/// One row's worth of change inside a <see cref="TimerRowsChangedEventArgs"/>
/// batch. <see cref="Catalog"/> is null only for <see cref="TimerRowChangeKind.Removed"/>;
/// <see cref="Progress"/> is null when the row exists but has never been started
/// (or was reset to idle).
/// </summary>
public sealed record TimerRowDelta(
    string Key,
    TimerRowChangeKind Kind,
    TimerCatalogEntry? Catalog,
    TimerProgressEntry? Progress);

public enum TimerRowChangeKind
{
    Added,
    CatalogChanged,
    ProgressChanged,
    Removed,
}

/// <summary>
/// Batched per-key change feed for <see cref="ITimerSource"/>. Sources coalesce
/// many simultaneous mutations (calibration overlay, character switch, journal
/// load) into a single event with N deltas — one event invocation, not N — so
/// consumers can apply all changes and call <c>ICollectionView.Refresh</c> at
/// most once per batch.
/// </summary>
public sealed class TimerRowsChangedEventArgs : EventArgs
{
    public required IReadOnlyList<TimerRowDelta> Deltas { get; init; }
}
