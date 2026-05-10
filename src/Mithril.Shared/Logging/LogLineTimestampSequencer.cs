namespace Mithril.Shared.Logging;

/// <summary>
/// Recovers absolute UTC timestamps for log lines whose only embedded time
/// information is a <c>[HH:MM:SS]</c> prefix (PG's gameplay log format).
/// The prefix is in <b>UTC</b> — verified against the same file's mtime
/// (Player.log mtime minus the player's TZ offset matches the prefix's
/// time-of-day) and against the ChatLog login banner's reported
/// <c>Timezone Offset</c>, which equals the offset between Player.log
/// prefixes and ChatLog lines. State is per-file: the date is anchored on
/// file mtime (UTC) for the first content batch and folded forward across
/// rollovers in subsequent batches. Lines without the prefix inherit the
/// previous line's stamp, or fall through to <see cref="TimeProvider.GetUtcNow"/>
/// if nothing has been seen yet.
///
/// Shared by <see cref="PlayerLogTailReader"/> (single-file Player.log)
/// and <see cref="ChatLogTailReader"/> (per-channel chat logs); each chat
/// file gets its own sequencer instance so dates fold independently.
/// Note: real chat-log lines don't carry the <c>[HH:MM:SS]</c> prefix
/// (their format is <c>yy-MM-dd HH:MM:SS\t...</c> in <i>local</i> time),
/// so chat lines fall through the prefix parse and rely on the inherited
/// stamp or wall-clock-now. A future chat-content parser that wants
/// absolute past-anchored stamps needs its own extraction path.
/// </summary>
internal sealed class LogLineTimestampSequencer
{
    private readonly TimeProvider _time;
    private DateOnly? _currentUtcDate;
    private TimeSpan? _prevUtcTimeOfDay;
    private DateTime _lastEmittedUtc;

    public LogLineTimestampSequencer(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Anchor the date for the first content batch. Pre-scans <paramref name="lines"/>
    /// for <c>[HH:MM:SS]</c> prefixes, counts midnight rollovers in the
    /// batch, and walks the start back so the LAST timestamped line lines
    /// up with file mtime (or mtime - 1 day if its tod is past mtime's
    /// tod, which happens when the file ticks over to the next UTC day
    /// shortly after the line was written). Caller passes a UTC mtime —
    /// both <see cref="PlayerLogTailReader"/> and <see cref="ChatLogTailReader"/>
    /// use <see cref="File.GetLastWriteTimeUtc"/> so the comparison
    /// against the UTC prefix is in one frame. No-op once anchored, and
    /// no-op for batches with no prefixed lines (defers init to a later
    /// batch that has at least one).
    /// </summary>
    public void EnsureAnchored(IReadOnlyList<string> lines, Func<DateTime> mtimeUtcAccessor)
    {
        if (_currentUtcDate is not null) return;

        var rollovers = 0;
        TimeSpan? prev = null;
        TimeSpan? lastSeen = null;
        foreach (var line in lines)
        {
            if (!TryParseTimestampPrefix(line, out var tod)) continue;
            if (prev.HasValue && tod < prev.Value && (prev.Value - tod) > TimeSpan.FromHours(12))
                rollovers++;
            prev = tod;
            lastSeen = tod;
        }

        if (lastSeen is null) return;

        DateTime mtimeUtc;
        try { mtimeUtc = mtimeUtcAccessor(); }
        catch { mtimeUtc = _time.GetUtcNow().UtcDateTime; }
        var anchorDate = DateOnly.FromDateTime(mtimeUtc);
        if (lastSeen.Value > mtimeUtc.TimeOfDay) anchorDate = anchorDate.AddDays(-1);

        _currentUtcDate = anchorDate.AddDays(-rollovers);
    }

    /// <summary>
    /// Compute the UTC stamp for a single log line. Parses the
    /// <c>[HH:MM:SS]</c> prefix (interpreted as UTC) and folds it over
    /// the current date, detecting midnight rollovers (HH wraps backward
    /// by &gt;12h). Lines without the prefix inherit the prior stamp; a
    /// leading run of unprefixed lines falls through to
    /// <see cref="TimeProvider.GetUtcNow"/>.
    /// </summary>
    public DateTime StampForLine(string line)
    {
        if (TryParseTimestampPrefix(line, out var tod) && _currentUtcDate.HasValue)
        {
            // Small backward jumps (out-of-order writes within the same
            // second, sub-second flush ordering) keep the same calendar
            // date; only a >12h backward gap signals a real midnight
            // rollover. UTC doesn't observe DST, so this threshold no
            // longer has to tolerate a 1h fall-back overlap — the slack
            // is kept as defense against pathological out-of-order log
            // writes the client could plausibly produce.
            if (_prevUtcTimeOfDay.HasValue
                && tod < _prevUtcTimeOfDay.Value
                && (_prevUtcTimeOfDay.Value - tod) > TimeSpan.FromHours(12))
            {
                _currentUtcDate = _currentUtcDate.Value.AddDays(1);
            }
            _prevUtcTimeOfDay = tod;

            var date = _currentUtcDate.Value;
            _lastEmittedUtc = new DateTime(date.Year, date.Month, date.Day,
                tod.Hours, tod.Minutes, tod.Seconds, DateTimeKind.Utc);
        }
        else if (_lastEmittedUtc == default)
        {
            _lastEmittedUtc = _time.GetUtcNow().UtcDateTime;
        }
        // else: inherit prior _lastEmittedUtc (engine noise interleaved
        // with gameplay lines stays anchored on the most recent gameplay tod).

        return _lastEmittedUtc;
    }

    /// <summary>
    /// Parse the <c>[HH:MM:SS] </c> prefix every PG gameplay line carries.
    /// Hand-rolled (vs regex) because this runs once per line on the seed
    /// replay path and the format is fixed-width.
    /// </summary>
    public static bool TryParseTimestampPrefix(string line, out TimeSpan tod)
    {
        tod = default;
        if (line.Length < 11) return false;
        if (line[0] != '[' || line[3] != ':' || line[6] != ':' || line[9] != ']' || line[10] != ' ') return false;
        if (!IsAsciiDigit(line[1]) || !IsAsciiDigit(line[2])) return false;
        if (!IsAsciiDigit(line[4]) || !IsAsciiDigit(line[5])) return false;
        if (!IsAsciiDigit(line[7]) || !IsAsciiDigit(line[8])) return false;
        var h = (line[1] - '0') * 10 + (line[2] - '0');
        var m = (line[4] - '0') * 10 + (line[5] - '0');
        var s = (line[7] - '0') * 10 + (line[8] - '0');
        if (h >= 24 || m >= 60 || s >= 60) return false;
        tod = new TimeSpan(h, m, s);
        return true;
    }

    private static bool IsAsciiDigit(char c) => (uint)(c - '0') <= 9;
}
