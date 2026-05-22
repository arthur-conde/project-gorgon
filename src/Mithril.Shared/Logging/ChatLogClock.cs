using System.Globalization;
using System.Text.RegularExpressions;

namespace Mithril.Shared.Logging;

/// <summary>
/// <see cref="ILogSourceClock"/> for chat logs. Parses the
/// <c>yy-MM-dd HH:mm:ss\t</c> prefix every PG chat-log line carries
/// (e.g. <c>26-04-25 15:10:48\t[Status] Shoddy Phlogiston x5 added to
/// inventory.</c>). The prefix is in <b>host-local</b> time — the game
/// process writes wall-clock with its local TZ. The clock folds the
/// parsed <see cref="DateTime"/> into a <see cref="DateTimeOffset"/>
/// using <b>the originating session's <c>Timezone Offset</c></b> when a
/// chat-side login banner has been observed, falling back to the
/// constructor-injected <see cref="TimeZoneInfo"/> (or
/// <see cref="TimeZoneInfo.Local"/>) for the pre-banner run.
///
/// <para><b>Why this matters (#183 root cause, #538 close).</b> Today
/// every consumer that touches <c>raw.Timestamp</c> for a chat-derived
/// line wraps it in <c>new DateTimeOffset(ts, TimeSpan.Zero)</c> —
/// which is wrong by the host's TZ offset. The chat tail-reader masked
/// the bug by falling through the prefix parse to wall-clock-now (which
/// happens to be UTC), so consumers never saw the mis-conversion in
/// practice. The L0 redesign typed every chat line with the host's TZ
/// at emission, which closed the *live-tail* half. #538 closes the
/// *replay* half: a chat log written on a UTC-7 machine and replayed on
/// a UTC+1 host would still be wrong by 8h with host-TZ-only folding.
/// Reading the originating offset from the per-session
/// <c>Logged In As … Timezone Offset HH:MM:SS.</c> banner removes the
/// gap — downstream of L0 the timestamp is correct regardless of which
/// machine wrote the log.</para>
///
/// <para><b>Deferred abstraction (no <c>IChatSessionAnchor</c>).</b>
/// The Player-side <see cref="ISessionAnchor"/> exists specifically to
/// break a DI cycle: <c>PlayerLogStream → anchor → GameSessionService →
/// PlayerLogStream</c>. On the chat side there is no analogous cycle —
/// <see cref="ChatLogClock"/> is consumed by chat-stream / tailer code
/// and nothing in <c>Mithril.GameState</c> writes chat-session state
/// back through them — so an interface + leaf-class pair would be pure
/// ceremony. The banner parse lives in-clock as a private static. If a
/// future need arises (a chat-session service in another assembly that
/// also needs the offset; cross-consumer offset sharing), the
/// extraction is mechanical: lift the private regex into a
/// <c>ChatBannerParser</c>, lift <see cref="_bannerOffset"/> into an
/// <c>IChatSessionAnchor</c> consumed alongside the existing TZ
/// fallback, wire the chat parser/banner consumer to write to it. The
/// emission semantics this class advertises don't change.</para>
///
/// <para><b>Hand-rolled prefix parse</b> (vs <c>DateTime.ParseExact</c>):
/// runs once per chat line on the seed-replay path; the format is
/// fixed-width; the consumer cost matters (cf. #507). Lines without the
/// prefix (rare — typically only the file's blank header lines) inherit
/// the prior emitted stamp; a leading run of unprefixed lines (before
/// any prefixed line has anchored <c>_lastEmitted</c>) falls through to
/// <see cref="TimeProvider.GetUtcNow"/> <em>re-tagged with the current
/// fallback offset</em>, so every value this clock emits carries the
/// same offset semantics — mixing Zero-offset (UTC) and local-offset
/// values from one source would break a future offset-comparing
/// consumer.</para>
///
/// <para><see cref="EnsureAnchored"/> is a no-op: the chat prefix
/// carries its own absolute date, so there is no equivalent of the
/// Player-side mtime/session-anchor back-walk to perform.</para>
/// </summary>
internal sealed partial class ChatLogClock : ILogSourceClock
{
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _fallbackTz;
    private DateTimeOffset? _lastEmitted;
    // Originating offset captured from the most recent
    // `Logged In As … Timezone Offset HH:MM:SS.` chat banner. Null until
    // the first banner is observed, after which it wins over
    // _fallbackTz for every subsequent stamp. Re-anchored on every new
    // banner (PG re-login during the same Mithril run).
    private TimeSpan? _bannerOffset;

    public ChatLogClock(TimeProvider time, TimeZoneInfo? fallbackTz = null, TimeSpan? seededBannerOffset = null)
    {
        _time = time;
        // Late-bind TimeZoneInfo.Local so a test can inject a fixed-offset
        // zone; the default is the host's actual local zone, which is
        // correct for the common live-tail case (the PG client wrote the
        // log on this same machine, so its local zone matches the
        // host's). The banner-derived offset, once seen, supersedes this
        // for the cross-machine-replay case (see #538).
        _fallbackTz = fallbackTz ?? TimeZoneInfo.Local;
        // Optional seed: a caller that has already scanned the session's
        // canonical login banner (e.g. ChatLogReplaySource, which scans
        // every chat file at attach to find the globally-most-recent
        // banner) can pre-seed the offset so all per-file clocks in the
        // session fold timestamps via the same offset from the very first
        // line. Without this, each file's first banner anchors its own
        // clock independently — files without a banner fall through to
        // `_fallbackTz`, which is wrong when the originating session's
        // offset differs from the replay host's local TZ. See #639.
        _bannerOffset = seededBannerOffset;
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
        // Banner recognition runs on every line (cheap regex against a
        // short string; the early-out on the prefix bound limits cost
        // for the long lines too). Updating _bannerOffset before the
        // fold ensures the banner *line itself* is also stamped with
        // its own offset, not the previous fallback — which is what
        // both Player and chat would observe if the offset changed mid-
        // session.
        if (TryParseBannerOffset(line, out var bannerOffset))
            _bannerOffset = bannerOffset;

        if (TryParseLocalDatePrefix(line, out var local))
        {
            // Prefer the banner-derived offset (once seen) over the
            // host-TZ fallback. The fallback-TZ path resolves via
            // TimeZoneInfo.GetUtcOffset, which handles DST correctly
            // (the offset varies per-instant); ambiguous-time / invalid-
            // time edge cases at DST transitions resolve to TimeZoneInfo's
            // default policy (treat ambiguous as standard, treat invalid
            // as if the clock had jumped), which matches how the game
            // itself recorded the line.
            var offset = _bannerOffset ?? _fallbackTz.GetUtcOffset(local);
            _lastEmitted = new DateTimeOffset(local, offset);
            return _lastEmitted.Value;
        }

        if (_lastEmitted is { } prior) return prior;
        // No prefix has anchored _lastEmitted yet (leading-unprefixed run,
        // typically chat-file header lines). Use wall-clock-now, but re-tag
        // with the current fallback offset so every value this clock emits
        // carries the same offset semantics — mixing Zero-offset (UTC) and
        // local-offset values from one source would break any future
        // offset-comparing consumer.
        var nowUtc = _time.GetUtcNow();
        var fallbackOffset = _bannerOffset ?? _fallbackTz.GetUtcOffset(nowUtc.UtcDateTime);
        _lastEmitted = nowUtc.ToOffset(fallbackOffset);
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

    // Chat-side login banner: e.g.
    // `26-05-19 21:01:14\t**************************************** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.`
    // Distinguished from the Player-side `Logged in as character …` by
    // the capitalisation (`Logged In As`) and the absence of a `Time
    // UTC=` field — the chat banner doesn't carry the UTC instant, only
    // the offset. Offset is signed `HH:MM:SS` (PG omits the `+` for
    // east-of-UTC), mirroring the LoginBannerParser shape.
    [GeneratedRegex(
        @"Logged In As [^.]+\. Server [^.]+\. Timezone Offset (?<off>-?\d{1,2}:\d{2}:\d{2})",
        RegexOptions.CultureInvariant)]
    private static partial Regex ChatBannerOffsetRx();

    private static bool TryParseBannerOffset(string line, out TimeSpan offset)
    {
        offset = default;
        // Cheap negative-match short-circuit: the literal substring is
        // present in every banner and absent from every non-banner chat
        // line. Skips the regex entirely on the 99%+ common case.
        if (line.IndexOf("Logged In As ", StringComparison.Ordinal) < 0) return false;

        var m = ChatBannerOffsetRx().Match(line);
        if (!m.Success) return false;

        return TimeSpan.TryParse(m.Groups["off"].Value, CultureInfo.InvariantCulture, out offset);
    }
}
