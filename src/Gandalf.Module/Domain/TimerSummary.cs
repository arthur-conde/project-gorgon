namespace Gandalf.Domain;

/// <summary>
/// Cross-source projection of one timer row, used by the Dashboard aggregator
/// and (in the future) the shell-level inbox. Every <see cref="ITimerSource"/>
/// maps its native shape into this uniform record so consumers don't need to
/// pattern-match the source's metadata.
///
/// <see cref="ExpiresAt"/> is null for Idle rows (no active cooldown). For
/// Cooling rows it's <c>StartedAt + Duration</c>; for Ready rows it's the same
/// timestamp (already past). Ready/Idle/Cooling/Done are mapped directly from
/// <see cref="TimerState"/>.
/// </summary>
public sealed record TimerSummary(
    string SourceId,
    string Key,
    string DisplayName,
    string? Region,
    DateTimeOffset? ExpiresAt,
    TimerState State);
