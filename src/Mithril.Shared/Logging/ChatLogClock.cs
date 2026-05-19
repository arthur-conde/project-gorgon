namespace Mithril.Shared.Logging;

/// <summary>
/// <see cref="ILogSourceClock"/> for chat logs. Parses the
/// <c>yy-MM-dd HH:mm:ss\t</c> prefix every PG chat-log line carries
/// (e.g. <c>26-04-25 15:10:48\t[Status] Shoddy Phlogiston x5 added to
/// inventory.</c>). The prefix is in <b>host-local</b> time — the game
/// process writes wall-clock with its local TZ — so the parsed
/// <see cref="DateTime"/> is folded into a <see cref="DateTimeOffset"/>
/// using <see cref="TimeZoneInfo.Local"/>'s UTC offset for that instant.
///
/// <para><b>Why this matters (#183 root cause).</b> Today every consumer
/// that touches <c>raw.Timestamp</c> for a chat-derived line wraps it in
/// <c>new DateTimeOffset(ts, TimeSpan.Zero)</c> — which is wrong by the
/// host's TZ offset. The chat tail-reader masked the bug by falling
/// through the prefix parse to wall-clock-now (which happens to be UTC),
/// so consumers never saw the mis-conversion in practice — but the
/// pattern is a tripwire: any consumer that did past-anchor on the chat
/// timestamp would silently shift state by the TZ offset. This clock
/// closes that gap structurally: downstream of L0 every chat line
/// already carries the TZ-correct <see cref="DateTimeOffset"/>, and the
/// per-consumer <c>TimeSpan.Zero</c> wrap (now a no-op on a
/// pre-typed value) goes away in the fan-out.</para>
///
/// <para><b>Hand-rolled prefix parse</b> (vs <c>DateTime.ParseExact</c>):
/// runs once per chat line on the seed-replay path; the format is
/// fixed-width; the consumer cost matters (cf. #507). Lines without the
/// prefix (rare — typically only the file's blank header lines) inherit
/// the prior emitted stamp; a leading run of unprefixed lines (before
/// any prefixed line has anchored <c>_lastEmitted</c>) falls through to
/// <see cref="TimeProvider.GetUtcNow"/> <em>re-tagged with the host's
/// local UTC offset</em>, so every value this clock emits carries the
/// same (local) offset semantics — mixing Zero-offset (UTC) and
/// local-offset values from one source would break a future
/// offset-comparing consumer.</para>
///
/// <para><see cref="EnsureAnchored"/> is a no-op: the chat prefix
/// carries its own absolute date, so there is no equivalent of the
/// Player-side mtime/session-anchor back-walk to perform.</para>
/// </summary>
internal sealed class ChatLogClock : ILogSourceClock
{
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _localTz;
    private DateTimeOffset? _lastEmitted;

    public ChatLogClock(TimeProvider time, TimeZoneInfo? localTz = null)
    {
        _time = time;
        // Late-bind TimeZoneInfo.Local so a test can inject a fixed-offset
        // zone; default is the host's actual local zone, which is what the
        // PG client writes against.
        _localTz = localTz ?? TimeZoneInfo.Local;
    }

    public void EnsureAnchored(IReadOnlyList<string> lines, Func<DateTime> mtimeUtcAccessor)
    {
        // No-op: the chat prefix encodes the absolute date, so there is
        // nothing to anchor. Provided to satisfy ILogSourceClock so a
        // future source whose grammar does need pre-stamp anchoring can
        // slot in without changing the tailer.
    }

    public DateTimeOffset StampForLine(string line)
    {
        if (TryParseLocalDatePrefix(line, out var local))
        {
            // The parsed DateTime is unspecified-kind wall-clock in the
            // host's local zone. Resolving via TimeZoneInfo.GetUtcOffset
            // handles DST correctly (the offset varies per-instant); the
            // ambiguous-time / invalid-time edge cases at DST transitions
            // resolve to TimeZoneInfo's default policy (treat ambiguous
            // as standard, treat invalid as if the clock had jumped),
            // which matches how the game itself recorded the line.
            var offset = _localTz.GetUtcOffset(local);
            _lastEmitted = new DateTimeOffset(local, offset);
            return _lastEmitted.Value;
        }

        if (_lastEmitted is { } prior) return prior;
        // No prefix has anchored _lastEmitted yet (leading-unprefixed run,
        // typically chat-file header lines). Use wall-clock-now, but re-tag
        // with the host's local UTC offset so every value this clock emits
        // carries the same offset semantics — mixing Zero-offset (UTC) and
        // local-offset values from one source would break any future
        // offset-comparing consumer.
        var nowUtc = _time.GetUtcNow();
        _lastEmitted = nowUtc.ToOffset(_localTz.GetUtcOffset(nowUtc.UtcDateTime));
        return _lastEmitted.Value;
    }

    /// <summary>
    /// Parse the <c>yy-MM-dd HH:mm:ss\t</c> prefix every PG chat-log line
    /// carries. Two-digit year (PG writes <c>26</c> not <c>2026</c>);
    /// resolved into the 2000s — PG's first release was 2014 so the
    /// epoch is unambiguous well into the next century.
    /// </summary>
    public static bool TryParseLocalDatePrefix(string line, out DateTime local)
    {
        local = default;
        // Minimum shape: "yy-MM-dd HH:mm:ss\t" → 18 characters.
        if (line.Length < 18) return false;
        if (line[2] != '-' || line[5] != '-' || line[8] != ' '
            || line[11] != ':' || line[14] != ':' || line[17] != '\t') return false;
        if (!Two(line, 0, out var yy)) return false;
        if (!Two(line, 3, out var mo)) return false;
        if (!Two(line, 6, out var dd)) return false;
        if (!Two(line, 9, out var hh)) return false;
        if (!Two(line, 12, out var mi)) return false;
        if (!Two(line, 15, out var ss)) return false;
        if (mo is < 1 or > 12) return false;
        if (dd is < 1 or > 31) return false;
        if (hh >= 24 || mi >= 60 || ss >= 60) return false;
        var year = 2000 + yy;
        try { local = new DateTime(year, mo, dd, hh, mi, ss, DateTimeKind.Unspecified); }
        catch (ArgumentOutOfRangeException) { return false; }  // 31 Feb etc.
        return true;
    }

    private static bool Two(string s, int i, out int v)
    {
        var a = s[i] - '0';
        var b = s[i + 1] - '0';
        if ((uint)a > 9 || (uint)b > 9) { v = 0; return false; }
        v = a * 10 + b;
        return true;
    }
}
