namespace Mithril.Shared.Logging;

/// <summary>
/// <see cref="ILogSourceClock"/> for Player.log. Recovers an absolute UTC
/// instant from the <c>[HH:MM:SS]</c> prefix every PG gameplay line
/// carries (UTC, no date — verified against the file's own mtime and the
/// ChatLog login banner's <c>Timezone Offset</c>), folded over a date
/// anchored from one of two sources:
///
/// <list type="bullet">
///   <item><b>Session anchor (preferred).</b> If an
///   <see cref="ISessionAnchor"/> has observed a <c>Logged in as
///   character … Time UTC=…</c> banner, pin <c>_currentUtcDate</c> to
///   <c>LoggedInUtc.Date</c> and prime <c>_prevUtcTimeOfDay</c> with the
///   login time-of-day. The login instant is the earliest known point in
///   the relevant window, so the forward-rollover detection in
///   <see cref="StampForLine"/> (HH wraps backward &gt; 12h ⇒ +1 day)
///   carries the date across every subsequent midnight without further
///   intervention. On <see cref="ISessionAnchor.AnchorChanged"/> (PG
///   re-login), re-anchor to the new banner's date. Self-describing from
///   inside the file — robust to copies, runs spanning midnight, and
///   clock drift.</item>
///   <item><b>mtime anchor (fallback).</b> No session known: walk the
///   date backward from the LAST timestamped line in the first batch,
///   subtracting in-batch rollover count, so the last line lines up with
///   the file's mtime (or mtime - 1 day if the line's tod is past
///   mtime's tod). Preserves pre-<see cref="ISessionAnchor"/> behaviour
///   for pre-banner lines (engine boot) and for the rare case where the
///   banner hasn't been parsed yet.</item>
/// </list>
///
/// Emits <see cref="DateTimeOffset"/> with offset
/// <see cref="TimeSpan.Zero"/> — Player.log is always UTC. (This is the
/// "boilerplate removal" half of #513: every consumer that today wraps
/// <c>raw.Timestamp</c> in <c>new DateTimeOffset(ts, TimeSpan.Zero)</c>
/// is wrapping a <see cref="DateTimeOffset"/> that already has the
/// correct offset; the wrap goes away.)
///
/// Renamed from <c>LogLineTimestampSequencer</c> as part of the L0
/// collapse — the sequencer was already shared by Player and chat tail
/// readers but was named after the Player-shaped grammar it implements;
/// the L0 redesign moves chat's actual grammar to
/// <see cref="ChatLogClock"/> and makes this class explicitly the
/// Player-side clock.
/// </summary>
internal sealed class PlayerLogClock : ILogSourceClock
{
    private readonly TimeProvider _time;
    private readonly ISessionAnchor? _sessionAnchor;
    private DateOnly? _currentUtcDate;
    private TimeSpan? _prevUtcTimeOfDay;
    private DateTime _lastEmittedUtc;
    // Tracks the session-anchor instant we've already aligned with so an
    // AnchorChanged event for a transition we've already absorbed doesn't
    // re-anchor mid-stream. Null when running under the mtime fallback.
    private DateTime? _appliedSessionAnchor;

    public PlayerLogClock(TimeProvider time, ISessionAnchor? sessionAnchor = null)
    {
        _time = time;
        _sessionAnchor = sessionAnchor;
        if (_sessionAnchor is not null)
            _sessionAnchor.AnchorChanged += OnAnchorChanged;
    }

    private void OnAnchorChanged(object? sender, EventArgs e)
    {
        // Drop the current anchor; the next StampForLine / EnsureAnchored
        // call will re-pick from the (now updated) session anchor.
        var anchor = _sessionAnchor?.LoggedInUtc;
        if (anchor is null) return;
        if (_appliedSessionAnchor == anchor) return;

        _currentUtcDate = DateOnly.FromDateTime(anchor.Value);
        _prevUtcTimeOfDay = anchor.Value.TimeOfDay;
        _appliedSessionAnchor = anchor;
    }

    public void EnsureAnchored(IReadOnlyList<string> lines, Func<DateTime> mtimeUtcAccessor)
    {
        if (_currentUtcDate is not null) return;

        // Prefer session anchor when available. EnsureAnchored runs at the
        // first batch from the tail reader; the session service's seed-replay
        // subscription typically lands the banner immediately, but if not we
        // fall through to the mtime path and OnAnchorChanged retros the date
        // when the banner does land.
        var anchor = _sessionAnchor?.LoggedInUtc;
        if (anchor is not null)
        {
            _currentUtcDate = DateOnly.FromDateTime(anchor.Value);
            _prevUtcTimeOfDay = anchor.Value.TimeOfDay;
            _appliedSessionAnchor = anchor;
            return;
        }

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

    public DateTimeOffset StampForLine(string line)
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

        return new DateTimeOffset(_lastEmittedUtc, TimeSpan.Zero);
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
