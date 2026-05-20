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
///   <item>Pair 3 with <see cref="TimeZoneInfo.Local"/>-flavoured
///   injection (rather than the originating banner's offset) is the
///   <b>gap-witness</b>: <see cref="ChatLogClock"/> today folds via
///   <see cref="TimeZoneInfo.Local"/>, which is wrong for replay of
///   another machine's chat logs. The skip-tagged fact below converts
///   that silent mis-conversion risk into an explicit, future-flippable
///   regression target.</item>
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

    // ── Pair 3 gap-witness (skipped) ───────────────────────────────
    // Today ChatLogClock folds via TimeZoneInfo.Local rather than the
    // originating banner's Timezone Offset. That is correct for the
    // common case (Mithril reading the host machine's own chat logs)
    // and wrong for replay of another machine's chat logs. This
    // fixture encodes the gap so the refactor tracked in #538 can flip
    // Skip to null and gain a green regression test in the same commit.

    [Fact(Skip = "Tracking gap #538 — ChatLogClock uses TimeZoneInfo.Local; flip Skip when #538 lands. See doc comment above for the full reason.")]
    public void Pair3_WithReplayMachineTimeZone_GapWitness()
    {
        // Simulate the user replaying the alt machine's chat log (whose
        // offset is UTC-7) on Arthur's machine (UTC+1). ChatLogClock
        // today reads TimeZoneInfo.Local — so the alt machine's
        // local-stamped `26-05-19 09:36:04` gets folded as if it were
        // 09:36:04 UTC+1 ⇒ 08:36:04 UTC, off by 8 hours from the true
        // 16:36:04 UTC instant. The assertion below is what we WANT to
        // hold; it will hold the moment ChatLogClock consumes the
        // banner's offset instead.
        var replayMachineTz = FixedOffsetZone(TimeSpan.FromHours(1));
        var clock = new ChatLogClock(TimeProvider.System, replayMachineTz);

        var stamp = clock.StampForLine(Pair3ChatBanner);

        stamp.UtcDateTime.Should().Be(Pair3ExpectedUtc);
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
