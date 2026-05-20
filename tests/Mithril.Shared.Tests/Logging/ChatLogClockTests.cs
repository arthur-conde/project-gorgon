using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// Fixture for the chat-side half of the cross-source UTC↔local
/// timestamp contract — see wiki Player-Log-Signals #cross-source-timestamp-contract
/// and issue #535. The corpus is three live-captured login-banner pairs
/// from 2026-05-19 (one same-day UTC+1, one cross-local-midnight UTC+1,
/// one opposite-sign UTC-7); the Player-side half lives in
/// <see cref="PlayerLogClockTests"/>.
///
/// <para>The contract this fixture pins:</para>
/// <list type="bullet">
///   <item>With the originating banner's <c>Timezone Offset</c> injected
///   as a fixed-offset <see cref="TimeZoneInfo"/>, <see cref="ChatLogClock"/>
///   round-trips the local-stamped chat line to a UTC instant that
///   exactly equals the corresponding Player.log <c>Time UTC=…</c> value.
///   This is the cross-source contract: the same physical event,
///   reconstructed identically from either log.</item>
///   <item>Pair 2 specifically witnesses that a chat-file rotation
///   across the local midnight (file rolls from <c>Chat-26-05-19.log</c>
///   to <c>Chat-26-05-20.log</c>) does not affect the round-trip — the
///   clock reads the in-line date prefix, not the filename, so the UTC
///   reconstruction lands on May 19 (the actual UTC day) regardless.</item>
///   <item>Pair 3 — alt machine's UTC-7 chat banner replayed on a
///   UTC+1 host TZ — round-trips correctly because <see cref="ChatLogClock"/>
///   reads the in-line <c>Logged In As … Timezone Offset HH:MM:SS.</c>
///   banner and folds via the banner's offset rather than the host TZ.
///   This was the #538 gap-witness; the fix lands with this fixture
///   green.</item>
///   <item>Pre-banner lines (the leading run before the
///   <c>Logged In As …</c> banner has been observed) fall through to
///   the injected/local TZ, preserving live-tail behaviour where the
///   host machine wrote the log.</item>
///   <item>A second <c>Logged In As</c> banner mid-stream (PG re-login)
///   re-anchors the offset for subsequent lines, the same way
///   <see cref="ISessionAnchor.AnchorChanged"/> re-anchors the Player
///   clock.</item>
/// </list>
/// </summary>
public sealed class ChatLogClockTests
{
    // ── Live capture corpus ────────────────────────────────────────
    // 2026-05-19 chat banners, hand-copied from real ChatLogs/*.log files.
    // Each pair below has a matching Player.log banner — see
    // PlayerLogClockTests for the Player-side half.

    // Pair 1 — same-day, UTC+1 (file Chat-26-05-19.log, character Emraell).
    private const string Pair1ChatBanner =
        "26-05-19 21:01:14\t**************************************** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.";
    private static readonly DateTime Pair1ExpectedUtc =
        new(2026, 5, 19, 20, 1, 14, DateTimeKind.Utc);
    private static readonly TimeSpan Pair1OriginOffset = TimeSpan.FromHours(1);

    // Pair 2 — cross-local-midnight, UTC+1 (file rotates to
    // Chat-26-05-20.log mid-session, character Emraell). The local
    // clock has rolled past midnight (00:08:03 on May 20) but the
    // matching Player.log banner is still 23:08:03 UTC on May 19 — so
    // the chat clock's job is to fold the local-date forward + offset
    // back to recover the UTC May 19 instant.
    private const string Pair2ChatBanner =
        "26-05-20 00:08:03\t**************************************** Logged In As Emraell. Server Laeth. Timezone Offset 01:00:00.";
    private static readonly DateTime Pair2ExpectedUtc =
        new(2026, 5, 19, 23, 8, 3, DateTimeKind.Utc);
    private static readonly TimeSpan Pair2OriginOffset = TimeSpan.FromHours(1);

    // Pair 3 — opposite-sign offset, UTC-7 (alt machine, file
    // Chat-26-05-19.log, character Praxi, Windows user mikew). Same
    // physical login as the Player.log Time UTC=05/19/2026 16:36:04
    // line; the chat side's local stamp is 09:36:04 because the alt
    // machine is 7h behind UTC.
    private const string Pair3ChatBanner =
        "26-05-19 09:36:04\t**************************************** Logged In As Praxi. Server Laeth. Timezone Offset -07:00:00.";
    private static readonly DateTime Pair3ExpectedUtc =
        new(2026, 5, 19, 16, 36, 4, DateTimeKind.Utc);
    private static readonly TimeSpan Pair3OriginOffset = TimeSpan.FromHours(-7);

    // ── Round-trip: chat banner ⇒ ChatLogClock ⇒ UTC ───────────────
    // With the originating banner's offset injected explicitly (NOT
    // TimeZoneInfo.Local — that would be wrong for replay of another
    // machine's logs, which is exactly the gap pair 3 witnesses below),
    // every pair round-trips to the matching Player.log UTC.

    [Theory]
    [InlineData(nameof(Pair1ChatBanner))]
    [InlineData(nameof(Pair2ChatBanner))]
    [InlineData(nameof(Pair3ChatBanner))]
    public void StampForLine_WithOriginatingOffsetInjected_RoundTripsToPlayerLogUtc(string pairName)
    {
        var (line, expectedUtc, originOffset) = LookupPair(pairName);
        var fixedOriginTz = FixedOffsetZone(originOffset);
        var clock = new ChatLogClock(TimeProvider.System, fixedOriginTz);

        var stamp = clock.StampForLine(line);

        stamp.UtcDateTime.Should().Be(expectedUtc);
        // Belt-and-braces: the emitted offset is the offset we injected
        // (the per-instant offset of the fixed-offset zone), so two
        // consumers comparing on Offset see consistent values across the
        // pair. Mixed-offset emission from one source would break a
        // future offset-comparing consumer.
        stamp.Offset.Should().Be(originOffset);
    }

    // ── Pair 2 specifically: local-midnight chat-file rotation ─────

    [Fact]
    public void Pair2_LocalMidnightRotation_DoesNotBreakRoundTrip()
    {
        // The chat clock reads the in-line `yy-MM-dd` date, not the
        // filename. So even though the chat line came from
        // Chat-26-05-20.log (post-rotation, local-day May 20), the UTC
        // reconstruction lands on May 19 — which is the correct day in
        // UTC terms and matches the Player.log Time UTC= for the same
        // physical login moment.
        var clock = new ChatLogClock(TimeProvider.System, FixedOffsetZone(Pair2OriginOffset));

        var stamp = clock.StampForLine(Pair2ChatBanner);

        stamp.UtcDateTime.Date.Should().Be(new DateTime(2026, 5, 19));
        stamp.UtcDateTime.Should().Be(Pair2ExpectedUtc);
        // Sanity: the local component is indeed the May 20 line — we
        // are exercising the rotation case, not a coincidence where the
        // local and UTC days happen to match.
        stamp.DateTime.Date.Should().Be(new DateTime(2026, 5, 20));
    }

    // ── Pair 3 banner-wins-over-injected-TZ (was #538 gap-witness) ─

    [Fact]
    public void Pair3_BannerOffsetWinsOverInjectedReplayMachineTimeZone()
    {
        // The user replays the alt machine's chat log (whose banner records
        // UTC-7) on Arthur's machine (UTC+1). The clock must reconstruct
        // 16:36:04 UTC from the alt-machine local stamp `09:36:04` by
        // reading the banner's `Timezone Offset -07:00:00.` — *not* by
        // folding through the injected UTC+1 (which would land 8 hours off
        // at 08:36:04 UTC). This is the #538 fix: in-clock banner parse
        // wins over the constructor-injected fallback TZ.
        var replayMachineTz = FixedOffsetZone(TimeSpan.FromHours(1));
        var clock = new ChatLogClock(TimeProvider.System, replayMachineTz);

        var stamp = clock.StampForLine(Pair3ChatBanner);

        stamp.UtcDateTime.Should().Be(Pair3ExpectedUtc);
        stamp.Offset.Should().Be(Pair3OriginOffset);
    }

    // ── Pre-banner fallback ─────────────────────────────────────────

    [Fact]
    public void StampForLine_BeforeBannerSeen_FoldsViaInjectedFallbackTimeZone()
    {
        // Lines that arrive before any `Logged In As …` banner (typical
        // chat-file headers, plus the rare leading content line on a
        // freshly-rotated file before login) preserve today's behaviour:
        // fold via the injected (or `TimeZoneInfo.Local`) fallback. The
        // banner-derived offset only kicks in once a banner has actually
        // been observed; this keeps live-tail on the host machine
        // bit-identical to pre-#538 behaviour.
        var fallbackTz = FixedOffsetZone(TimeSpan.FromHours(5));
        var clock = new ChatLogClock(TimeProvider.System, fallbackTz);
        const string preBannerStatusLine =
            "26-05-19 12:00:00\t[Status] Some pre-login status notification.";

        var stamp = clock.StampForLine(preBannerStatusLine);

        // 12:00:00 local in UTC+5 ⇒ 07:00:00 UTC.
        stamp.UtcDateTime.Should().Be(new DateTime(2026, 5, 19, 7, 0, 0, DateTimeKind.Utc));
        stamp.Offset.Should().Be(TimeSpan.FromHours(5));
    }

    // ── Mid-stream re-anchor on a second banner (PG re-login) ──────

    [Fact]
    public void StampForLine_SecondBannerMidStream_ReAnchorsOffsetForSubsequentLines()
    {
        // PG re-login during the same Mithril run: a second
        // `Logged In As …` banner with a different `Timezone Offset`
        // value must re-anchor the offset for subsequent chat lines,
        // mirroring the Player-clock's response to
        // ISessionAnchor.AnchorChanged. Without re-anchoring, the chat
        // clock would silently keep using the first banner's offset and
        // mis-convert every post-relogin line by the offset delta.
        //
        // Sequence: Pair-1 banner (UTC+1) ⇒ generic line under UTC+1 ⇒
        // Pair-3 banner (UTC-7) ⇒ generic line under UTC-7. Asserting
        // the *generic* lines (not the banners themselves) is what
        // proves the offset state carried forward correctly.
        var clock = new ChatLogClock(TimeProvider.System);
        const string postPair1Line =
            "26-05-19 21:05:00\t[Status] Generic line after Pair-1 banner.";
        const string postPair3Line =
            "26-05-19 10:00:00\t[Status] Generic line after Pair-3 banner.";

        clock.StampForLine(Pair1ChatBanner);
        var firstFollowup = clock.StampForLine(postPair1Line);
        clock.StampForLine(Pair3ChatBanner);
        var secondFollowup = clock.StampForLine(postPair3Line);

        // 21:05:00 local in UTC+1 ⇒ 20:05:00 UTC (Pair-1 offset).
        firstFollowup.UtcDateTime.Should().Be(new DateTime(2026, 5, 19, 20, 5, 0, DateTimeKind.Utc));
        firstFollowup.Offset.Should().Be(Pair1OriginOffset);

        // 10:00:00 local in UTC-7 ⇒ 17:00:00 UTC (Pair-3 offset).
        secondFollowup.UtcDateTime.Should().Be(new DateTime(2026, 5, 19, 17, 0, 0, DateTimeKind.Utc));
        secondFollowup.Offset.Should().Be(Pair3OriginOffset);
    }

    // ── Helpers ────────────────────────────────────────────────────

    private static (string Line, DateTime ExpectedUtc, TimeSpan OriginOffset) LookupPair(string pairName) => pairName switch
    {
        nameof(Pair1ChatBanner) => (Pair1ChatBanner, Pair1ExpectedUtc, Pair1OriginOffset),
        nameof(Pair2ChatBanner) => (Pair2ChatBanner, Pair2ExpectedUtc, Pair2OriginOffset),
        nameof(Pair3ChatBanner) => (Pair3ChatBanner, Pair3ExpectedUtc, Pair3OriginOffset),
        _ => throw new ArgumentOutOfRangeException(nameof(pairName), pairName, "unknown pair"),
    };

    private static TimeZoneInfo FixedOffsetZone(TimeSpan offset)
    {
        // CreateCustomTimeZone doesn't register in any global table —
        // each call returns a fresh instance — so the id only needs to
        // be human-readable, not globally unique.
        var id = $"FixedOffsetTestZone_{offset.Ticks}";
        return TimeZoneInfo.CreateCustomTimeZone(id, offset, id, id);
    }
}
