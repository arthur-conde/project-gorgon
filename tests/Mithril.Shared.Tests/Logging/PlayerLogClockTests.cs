using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests.Logging;

/// <summary>
/// Fixture for the Player-side half of the cross-source UTC↔local
/// timestamp contract — see wiki Player-Log-Signals #cross-source-timestamp-contract
/// and issue #535. The corpus is three live-captured login-banner pairs
/// from 2026-05-19 (one same-day UTC+1, one cross-local-midnight UTC+1,
/// one opposite-sign UTC-7); the chat-side half lives in
/// <see cref="ChatLogClockTests"/>.
///
/// <para>The contract this fixture pins:</para>
/// <list type="bullet">
///   <item><see cref="PlayerLogClock"/> emits a <see cref="DateTimeOffset"/>
///   whose <see cref="DateTimeOffset.UtcDateTime"/> exactly equals the
///   <c>Time UTC=…</c> value in the banner.</item>
///   <item>The emitted offset is <see cref="TimeSpan.Zero"/> — Player.log
///   is always UTC, and that is the load-bearing assumption every
///   downstream consumer relies on.</item>
///   <item>The <c>[HH:MM:SS]</c> bracket value matches <c>Time UTC=…</c>'s
///   time-of-day byte-for-byte. This is the property the session-anchor
///   path silently relies on; asserting it directly converts a future
///   log-format change into a test failure rather than a runtime drift.</item>
/// </list>
/// </summary>
public sealed class PlayerLogClockTests
{
    // ── Live capture corpus ────────────────────────────────────────
    // 2026-05-19 login banners, hand-copied from real Player.log files.
    // Each pair below also has a matching chat banner — see
    // ChatLogClockTests for the chat-side half.

    // Pair 1 — same-day, UTC+1 (Arthur's machine, character Emraell).
    private const string Pair1PlayerBanner =
        "[20:01:14] Logged in as character Emraell. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00";
    private static readonly DateTime Pair1ExpectedUtc =
        new(2026, 5, 19, 20, 1, 14, DateTimeKind.Utc);

    // Pair 2 — cross-local-midnight, UTC+1 (Arthur's machine, character
    // Emraell). Local clock is past midnight (00:08:03 on May 20) but the
    // UTC banner is still on May 19; this is the case that exercises
    // chat-file rotation on the chat side, and on the Player side
    // exercises "UTC date != local date at this moment."
    private const string Pair2PlayerBanner =
        "[23:08:03] Logged in as character Emraell. Time UTC=05/19/2026 23:08:03. Timezone Offset 01:00:00";
    private static readonly DateTime Pair2ExpectedUtc =
        new(2026, 5, 19, 23, 8, 3, DateTimeKind.Utc);

    // Pair 3 — opposite-sign offset, UTC-7 (alt machine, character Praxi,
    // Windows user mikew). The minus-sign capture rules out a
    // "+offset only" coincidence; if a refactor added an unsigned-offset
    // parse error this is the test that would catch it.
    private const string Pair3PlayerBanner =
        "[16:36:04] Logged in as character Praxi. Time UTC=05/19/2026 16:36:04. Timezone Offset -07:00:00";
    private static readonly DateTime Pair3ExpectedUtc =
        new(2026, 5, 19, 16, 36, 4, DateTimeKind.Utc);

    // ── Round-trip: banner ⇒ PlayerLogClock ⇒ UTC ──────────────────

    [Theory]
    [InlineData(nameof(Pair1PlayerBanner))]
    [InlineData(nameof(Pair2PlayerBanner))]
    [InlineData(nameof(Pair3PlayerBanner))]
    public void StampForLine_WithSessionAnchorPrimedFromBanner_ProducesBannerUtc(string pairName)
    {
        var (line, expectedUtc) = LookupPair(pairName);
        var anchor = new FakeSessionAnchor(expectedUtc);
        var clock = new PlayerLogClock(TimeProvider.System, anchor);
        // EnsureAnchored reads the anchor and pins _currentUtcDate; the
        // mtime accessor is dead in this path (the anchor pre-empts it)
        // so it's deliberately set to a value that would be wrong if the
        // anchor weren't honoured.
        clock.EnsureAnchored(new[] { line }, () => new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var stamp = clock.StampForLine(line);

        stamp.UtcDateTime.Should().Be(expectedUtc);
    }

    [Theory]
    [InlineData(nameof(Pair1PlayerBanner))]
    [InlineData(nameof(Pair2PlayerBanner))]
    [InlineData(nameof(Pair3PlayerBanner))]
    public void StampForLine_EmittedOffsetIsZero(string pairName)
    {
        var (line, expectedUtc) = LookupPair(pairName);
        var anchor = new FakeSessionAnchor(expectedUtc);
        var clock = new PlayerLogClock(TimeProvider.System, anchor);
        clock.EnsureAnchored(new[] { line }, () => expectedUtc);

        var stamp = clock.StampForLine(line);

        stamp.Offset.Should().Be(TimeSpan.Zero);
    }

    // ── Fixture self-check: bracket prefix == Time UTC= time-of-day ─

    [Theory]
    [InlineData(nameof(Pair1PlayerBanner))]
    [InlineData(nameof(Pair2PlayerBanner))]
    [InlineData(nameof(Pair3PlayerBanner))]
    public void BannerBracketPrefix_MatchesTimeUtcTimeOfDay_ByteForByte(string pairName)
    {
        // The PlayerLogClock contract — and every downstream consumer
        // that treats `[HH:MM:SS]` as UTC — relies on the bracket prefix
        // being the time-of-day component of the banner's `Time UTC=`
        // value, character-for-character. If the PG client ever changed
        // the prefix to local-time, this assertion fires before any
        // round-trip test would notice.
        var (line, _) = LookupPair(pairName);
        var bracket = line.Substring(1, 8);  // "[HH:MM:SS] …" → "HH:MM:SS"

        var timeUtcMarker = line.IndexOf("Time UTC=", StringComparison.Ordinal);
        timeUtcMarker.Should().BeGreaterThan(0,
            "the banner format used to encode the UTC instant inline");
        // "Time UTC=MM/DD/YYYY HH:MM:SS." — skip "Time UTC=" (9) + "MM/DD/YYYY " (11) → +20
        var fromTimeUtc = line.Substring(timeUtcMarker + 20, 8);

        fromTimeUtc.Should().Be(bracket);
    }

    private static (string Line, DateTime ExpectedUtc) LookupPair(string pairName) => pairName switch
    {
        nameof(Pair1PlayerBanner) => (Pair1PlayerBanner, Pair1ExpectedUtc),
        nameof(Pair2PlayerBanner) => (Pair2PlayerBanner, Pair2ExpectedUtc),
        nameof(Pair3PlayerBanner) => (Pair3PlayerBanner, Pair3ExpectedUtc),
        _ => throw new ArgumentOutOfRangeException(nameof(pairName), pairName, "unknown pair"),
    };

    private sealed class FakeSessionAnchor : ISessionAnchor
    {
        public FakeSessionAnchor(DateTime loggedInUtc) { LoggedInUtc = loggedInUtc; }
        public DateTime? LoggedInUtc { get; }
        public event EventHandler? AnchorChanged { add { } remove { } }
    }
}
