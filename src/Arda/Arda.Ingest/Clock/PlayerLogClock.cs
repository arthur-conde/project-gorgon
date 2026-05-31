using System.Globalization;

namespace Arda.Ingest.Clock;

/// <summary>
/// <see cref="ILogSourceClock"/> for Player.log. Parses the <c>[HH:MM:SS] </c>
/// prefix (11 characters: bracket, 2-digit hour, colon, 2-digit minute, colon,
/// 2-digit second, bracket, space). The time is UTC with no date component.
/// <para>
/// Date derivation uses two strategies in priority order:
/// <list type="bullet">
///   <item><b>Session anchor (preferred).</b> If the coordinator has observed a
///   login banner with <c>Time UTC=...</c>, the date is pinned from that instant.
///   Forward-rollover detection (HH wraps backward &gt; 12h) advances the date
///   across midnight.</item>
///   <item><b>mtime fallback.</b> Walks the last line's time-of-day backward from
///   the file's last-write-time to derive the starting date for the batch.</item>
/// </list>
/// </para>
/// <para>
/// Emits <see cref="DateTimeOffset"/> with offset <see cref="TimeSpan.Zero"/>
/// — Player.log is always UTC.
/// </para>
/// </summary>
internal sealed class PlayerLogClock : ILogSourceClock
{
    private readonly TimeProvider _time;
    private DateOnly? _currentUtcDate;
    private TimeSpan? _prevTimeOfDay;

    internal const int PrefixLength = 11; // "[HH:MM:SS] "

    // Slack allowed between the last line's time-of-day and the file mtime before
    // the mtime-fallback decides the line belongs to the previous day. Live-log
    // buffering can leave the newest line a few seconds ahead of a lagging mtime;
    // a genuine prior-day last line sits many hours ahead, so an hour cleanly
    // separates the two cases (#942).
    private static readonly TimeSpan MtimeRollbackTolerance = TimeSpan.FromHours(1);

    public PlayerLogClock(TimeProvider time)
    {
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <inheritdoc/>
    public ClockResult TryParse(ReadOnlySpan<char> line)
    {
        if (line.Length < PrefixLength)
            return ClockResult.None;

        if (line[0] != '[' || line[3] != ':' || line[6] != ':' || line[9] != ']')
            return ClockResult.None;

        if (!int.TryParse(line[1..3], out var hour) ||
            !int.TryParse(line[4..6], out var minute) ||
            !int.TryParse(line[7..9], out var second))
            return ClockResult.None;

        if (hour > 23 || minute > 59 || second > 59)
            return ClockResult.None;

        var timeOfDay = new TimeSpan(hour, minute, second);

        if (_currentUtcDate is null)
        {
            // Defensive fallback: not the expected path. Normal flow calls
            // EnsureAnchored (with mtime) before the first TryParse. This
            // branch covers unexpected call ordering gracefully by deriving
            // the date from wall-clock time.
            var now = _time.GetUtcNow();
            _currentUtcDate = DateOnly.FromDateTime(now.UtcDateTime);
        }

        // Midnight rollover: if time-of-day jumped backward by more than 12h,
        // a new UTC day started.
        if (_prevTimeOfDay.HasValue && timeOfDay < _prevTimeOfDay.Value &&
            (_prevTimeOfDay.Value - timeOfDay).TotalHours > 12)
        {
            _currentUtcDate = _currentUtcDate.Value.AddDays(1);
        }

        _prevTimeOfDay = timeOfDay;

        var dt = _currentUtcDate.Value.ToDateTime(TimeOnly.FromTimeSpan(timeOfDay));
        var timestamp = new DateTimeOffset(dt, TimeSpan.Zero);

        return new ClockResult(timestamp, PrefixLength);
    }

    /// <inheritdoc/>
    public void EnsureAnchored(
        ReadOnlySpan<(int Start, int Length)> firstBatchLines,
        char[] buffer,
        Func<DateTime> mtimeUtcAccessor)
    {
        if (_currentUtcDate.HasValue)
            return;

        // Walk the batch to find the last line with a valid timestamp,
        // then back-derive the date from the file's mtime.
        TimeSpan? lastTimeOfDay = null;
        var midnightRollovers = 0;
        TimeSpan? prevTod = null;

        for (var i = 0; i < firstBatchLines.Length; i++)
        {
            var (start, length) = firstBatchLines[i];
            var line = buffer.AsSpan(start, length);

            if (line.Length < PrefixLength) continue;
            if (line[0] != '[' || line[3] != ':' || line[6] != ':' || line[9] != ']') continue;

            if (!int.TryParse(line[1..3], out var h) ||
                !int.TryParse(line[4..6], out var m) ||
                !int.TryParse(line[7..9], out var s))
                continue;

            var tod = new TimeSpan(h, m, s);

            if (prevTod.HasValue && tod < prevTod.Value &&
                (prevTod.Value - tod).TotalHours > 12)
            {
                midnightRollovers++;
            }

            prevTod = tod;
            lastTimeOfDay = tod;
        }

        if (lastTimeOfDay is null)
        {
            _currentUtcDate = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
            return;
        }

        var mtime = mtimeUtcAccessor();
        var mtimeDate = DateOnly.FromDateTime(mtime);

        // Place the last line's time-of-day on the mtime's date. The last line
        // cannot have been written meaningfully *after* the file's last-write-
        // time, so if that candidate lands beyond mtime by more than a small
        // tolerance the file rolled past midnight before its final flush and the
        // line belongs to the previous day. A bare time-of-day ">" comparison
        // misfires on a live log whose newest buffered line is a few seconds
        // ahead of a lagging mtime, rolling the whole anchor back a day (#942).
        var candidate = mtimeDate.ToDateTime(TimeOnly.FromTimeSpan(lastTimeOfDay.Value));
        if (candidate - mtime > MtimeRollbackTolerance)
            mtimeDate = mtimeDate.AddDays(-1);

        _currentUtcDate = mtimeDate.AddDays(-midnightRollovers);
        _prevTimeOfDay = null; // Will be set on first TryParse call
    }

    /// <summary>
    /// Externally anchor the clock to a known UTC date (e.g., from a
    /// login banner). Resets rollover tracking.
    /// </summary>
    internal void AnchorToDate(DateOnly utcDate, TimeSpan timeOfDay)
    {
        _currentUtcDate = utcDate;
        _prevTimeOfDay = timeOfDay;
    }

    private const string BannerMarker = "Logged in as character ";
    private const string TimeMarker = "Time UTC=";

    // The game emits the login instant via invariant-culture DateTime.ToString():
    // "MM/dd/yyyy HH:mm:ss" (slashes, month-first), e.g.
    //   Time UTC=05/31/2026 12:55:00. Timezone Offset 01:00:00
    private const string BannerDateTimeFormat = "MM/dd/yyyy HH:mm:ss";

    /// <inheritdoc/>
    public void TryConsumeBanner(ReadOnlySpan<char> line)
    {
        // Cheap shape check: line must carry a parseable [HH:MM:SS] prefix.
        if (line.Length < PrefixLength) return;
        if (line[0] != '[' || line[3] != ':' || line[6] != ':' || line[9] != ']') return;

        var body = line[PrefixLength..];
        if (!body.StartsWith(BannerMarker.AsSpan(), StringComparison.Ordinal)) return;

        var timeIdx = body.IndexOf(TimeMarker.AsSpan(), StringComparison.Ordinal);
        if (timeIdx < 0) return;

        var stamp = body[(timeIdx + TimeMarker.Length)..];
        // Take the fixed-width "MM/dd/yyyy HH:mm:ss" datetime prefix; the banner
        // continues with ". Timezone Offset ..." which we ignore here (Time UTC=
        // is already UTC, so Player.log anchors with a zero offset).
        if (stamp.Length < BannerDateTimeFormat.Length) return;
        var dateTime = stamp[..BannerDateTimeFormat.Length];

        if (!DateTime.TryParseExact(dateTime, BannerDateTimeFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc))
            return;

        // A new banner crosses a session boundary — reset rollover tracking
        // before re-anchoring so the next line doesn't trigger a false
        // midnight advance against the prior session's last time-of-day.
        Reset();
        AnchorToDate(DateOnly.FromDateTime(utc), utc.TimeOfDay);
    }

    /// <summary>
    /// Clear the anchored date and rollover tracking. Used when a session
    /// boundary is crossed (e.g., mid-session Player.log rotation) so the
    /// next batch re-anchors via banner or mtime.
    /// </summary>
    internal void Reset()
    {
        _currentUtcDate = null;
        _prevTimeOfDay = null;
    }
}
