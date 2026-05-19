using System.IO;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests;

// Several tests in this file are anchored on **live captures from a real
// Project Gorgon Player.log**, taken from %LocalAppData%Low\Elder Game\
// Project Gorgon\Player.log on 2026-05-19 (character Emraell). They are
// tagged "LIVE CAPTURE 2026-05-19" inline. The point of mixing live and
// synthetic input is twofold: (a) live input proves the parser doesn't
// rely on shapes that only happen in our test fixtures (e.g. the real
// session banner's `Time UTC=05/19/2026 20:01:14. Timezone Offset
// 01:00:00` is wider and has a trailing `:00` we'd never write by hand);
// (b) synthetic input is still required for the edge-case tests
// (midnight rollover, out-of-order writes, truncation) where the real
// log doesn't conveniently contain the shape under test.

[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class PlayerLogTailReaderTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public PlayerLogTailReaderTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-tailreader");
        _logPath = Path.Combine(_dir, "Player.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ReadNew_RecoversAbsoluteTimestampFromBracketedPrefix()
    {
        // Anchors the contract: the [HH:MM:SS] prefix is in UTC (PG writes
        // UTC into Player.log; verified against the file's own mtime and
        // against ChatLog's "Timezone Offset" login banner). The clock
        // folds the tod over file-mtime-UTC to produce an absolute UTC stamp.
        //
        // ── LIVE CAPTURE 2026-05-19 ────────────────────────────────────
        // Three consecutive Player.log lines from the live session right
        // after login (Player.log, character Emraell, 2026-05-19 20:01:18
        // UTC). Real verbs, real argument shapes — the test fails the
        // same way it would fail in production if the prefix parser
        // regressed on real input, not just on the test-fixture shape.
        File.WriteAllText(_logPath,
            "[20:01:18] LocalPlayer: ProcessSetAreaSettings(AreaSettingsFromServer)\n" +
            "[20:01:18] LocalPlayer: ProcessSetWeather(\"Clear Sky\", True)\n" +
            "[20:01:18] LocalPlayer: ProcessSetCelestialInfo(WaxingCrescentMoon)\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 19, 20, 1, 18, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(3);
        lines[0].Timestamp.Should().Be(new DateTime(2026, 5, 19, 20, 1, 18, DateTimeKind.Utc));
        lines[1].Timestamp.Should().Be(new DateTime(2026, 5, 19, 20, 1, 18, DateTimeKind.Utc));
        lines[2].Timestamp.Should().Be(new DateTime(2026, 5, 19, 20, 1, 18, DateTimeKind.Utc));
        lines[1].Line.Should().Contain("ProcessSetWeather(\"Clear Sky\", True)");
    }

    [Fact]
    public void ReadNew_AcrossMidnight_FoldsDateForward()
    {
        // Marathon session crossing UTC midnight. The pre-midnight line's
        // [HH:MM:SS] (23:55) is greater than the post-midnight line's
        // (00:05); the >12h backward gap is the rollover signal, so the
        // post-midnight line gets tagged with the next calendar day.
        File.WriteAllText(_logPath,
            "[23:55:00] line-pre-midnight\n" +
            "[00:05:00] line-post-midnight\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 0, 5, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(2);
        lines[0].Timestamp.Should().Be(new DateTime(2026, 5, 10, 23, 55, 0, DateTimeKind.Utc));
        lines[1].Timestamp.Should().Be(new DateTime(2026, 5, 11, 0, 5, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_SmallBackwardJump_DoesNotTriggerRollover()
    {
        // Sub-12h backward jumps (e.g. out-of-order flushes within the same
        // hour, or any pathological non-monotonic write the client could
        // produce) must not be misread as midnight rollovers. Only a >12h
        // backward gap counts as a real rollover. The threshold used to
        // exist to tolerate a 1h DST fall-back overlap; now that the
        // prefix is UTC (DST-free) the slack is purely defense against
        // out-of-order writes.
        File.WriteAllText(_logPath,
            "[02:30:00] line-earlier-tod\n" +
            "[02:25:00] line-later-but-tod-rewound\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 2, 30, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(2);
        lines[0].Timestamp.Should().Be(new DateTime(2026, 5, 10, 2, 30, 0, DateTimeKind.Utc));
        lines[1].Timestamp.Should().Be(new DateTime(2026, 5, 10, 2, 25, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_UnprefixedLine_InheritsPreviousTimestamp()
    {
        // Unity engine noise (stack traces, init banners) lacks the [HH:MM:SS]
        // prefix. No parser cares about these lines but the contract is
        // "stamp consistently": inherit the most recent gameplay-line tod
        // rather than re-stamp with wall-clock-now mid-batch.
        File.WriteAllText(_logPath,
            "[10:00:00] LocalPlayer: ProcessAddPlayer(1, 2, \"desc\", \"TestChar\")\n" +
            "NullReferenceException: Object reference not set to an instance of an object.\n" +
            "[10:00:05] LocalPlayer: next gameplay line\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 10, 0, 5, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(3);
        lines[0].Timestamp.Should().Be(new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc));
        lines[1].Timestamp.Should().Be(lines[0].Timestamp);  // inherited
        lines[2].Timestamp.Should().Be(new DateTime(2026, 5, 10, 10, 0, 5, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_LeadingUnprefixedLines_FallThroughToTimeProviderNow()
    {
        // Engine init banners arrive before any [HH:MM:SS]. With no prior
        // gameplay tod to inherit, fall through to wall-clock-now. Fakable so
        // CI doesn't have to depend on real-time progression.
        File.WriteAllText(_logPath,
            "Initialize engine version: 6000.3.11f1\n" +
            "[Physics::Module] Initialized fallback backend.\n" +
            "[10:00:00] LocalPlayer: first gameplay line\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc));

        var fixedNow = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc);
        var time = new FixedTimeProvider(fixedNow);

        var reader = new PlayerLogTailReader(_logPath, time);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(3);
        lines[0].Timestamp.Should().Be(fixedNow);
        lines[1].Timestamp.Should().Be(fixedNow);  // inherited from line 0's fallback
        lines[2].Timestamp.Should().Be(new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_AcrossBatches_PreservesRolloverState()
    {
        // First batch ends just before UTC midnight, second batch starts just
        // after. The reader must carry _currentUtcDate + _prevUtcTimeOfDay
        // across the batch boundary so the post-midnight line gets the next
        // day — not the same day as the pre-midnight line.
        File.WriteAllText(_logPath, "[23:55:00] pre-midnight\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 23, 55, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var first = reader.ReadNew();
        first.Should().ContainSingle()
            .Which.Timestamp.Should().Be(new DateTime(2026, 5, 10, 23, 55, 0, DateTimeKind.Utc));

        File.AppendAllText(_logPath, "[00:05:00] post-midnight\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 0, 5, 0, DateTimeKind.Utc));

        var second = reader.ReadNew();
        second.Should().ContainSingle()
            .Which.Timestamp.Should().Be(new DateTime(2026, 5, 11, 0, 5, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_LastLineTodPastMtimeTimeOfDay_BacksAnchorUpOneDay()
    {
        // Late-night session: the final [HH:MM:SS] is at 23:59 but the file's
        // mtime ticked over to 00:01 the next UTC day (write-buffer flush
        // happens after midnight). The line was actually written yesterday;
        // the anchor must back up one day so we don't tag it with tomorrow.
        File.WriteAllText(_logPath, "[23:59:00] last-line\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 0, 1, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().ContainSingle()
            .Which.Timestamp.Should().Be(new DateTime(2026, 5, 10, 23, 59, 0, DateTimeKind.Utc));
    }

    // ── Session-anchor scenarios ────────────────────────────────────

    [Fact]
    public void ReadNew_WithSessionAnchor_PinsDateToLoggedInUtcInsteadOfMtime()
    {
        // The session banner pins 2026-05-11. Even if the file's mtime is a
        // year off, the session anchor wins. Catches the mtime-drift case
        // (copied file, system clock skew, stale on-disk metadata).
        File.WriteAllText(_logPath,
            "[12:25:04] LocalPlayer: ProcessAddPlayer(1, 2, \"\", \"Emraell\")\n" +
            "[12:30:00] LocalPlayer: ProcessCompleteQuest(1, 1)\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var anchor = new FakeSessionAnchor(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
        var reader = new PlayerLogTailReader(_logPath, sessionAnchor: anchor);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(2);
        lines[0].Timestamp.Should().Be(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
        lines[1].Timestamp.Should().Be(new DateTime(2026, 5, 11, 12, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_WithSessionAnchor_CrossesMidnightInLiveStream()
    {
        // Login at 22:00; live stream at 23:59:55 then 00:00:05. Verifies
        // the forward-rollover detection still fires under session anchoring,
        // and that priming _prevUtcTimeOfDay with the login tod doesn't
        // mis-fire a rollover on the first stamped line.
        File.WriteAllText(_logPath, "[22:00:00] LocalPlayer: login moment\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 22, 0, 0, DateTimeKind.Utc));

        var anchor = new FakeSessionAnchor(new DateTime(2026, 5, 10, 22, 0, 0, DateTimeKind.Utc));
        var reader = new PlayerLogTailReader(_logPath, sessionAnchor: anchor);
        reader.ReadNew();

        File.AppendAllText(_logPath, "[23:59:55] just-before-midnight\n[00:00:05] just-after-midnight\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 0, 0, 5, DateTimeKind.Utc));

        var second = reader.ReadNew();
        second.Should().HaveCount(2);
        second[0].Timestamp.Should().Be(new DateTime(2026, 5, 10, 23, 59, 55, DateTimeKind.Utc));
        second[1].Timestamp.Should().Be(new DateTime(2026, 5, 11, 0, 0, 5, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_AnchorChanged_ReAnchorsToNewSession()
    {
        // First batch under session A. Then a second login (PG re-login)
        // raises AnchorChanged with a different LoggedInUtc — possibly on a
        // different calendar day. Subsequent lines must stamp against the
        // new date.
        File.WriteAllText(_logPath, "[12:00:00] LocalPlayer: line-one\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));

        var anchor = new FakeSessionAnchor(new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc));
        var reader = new PlayerLogTailReader(_logPath, sessionAnchor: anchor);
        var first = reader.ReadNew();
        first.Should().ContainSingle()
            .Which.Timestamp.Should().Be(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));

        // Simulate PG re-login on the next UTC day.
        anchor.Set(new DateTime(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc));

        File.AppendAllText(_logPath, "[10:30:00] LocalPlayer: post-relogin\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 10, 30, 0, DateTimeKind.Utc));

        var second = reader.ReadNew();
        second.Should().ContainSingle()
            .Which.Timestamp.Should().Be(new DateTime(2026, 5, 11, 10, 30, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_WithSessionAnchor_DoesNotMisFireRolloverOnFirstStampedLine()
    {
        // First gameplay line's tod is *earlier* than the login tod (e.g. the
        // engine emits a quick scripted line at 12:25:03 right after the
        // 12:25:04 banner). The seed `_prevUtcTimeOfDay = LoggedInUtc.tod`
        // means the next StampForLine sees `tod < prev` — but the delta is
        // ~1 second, far below the 12h threshold, so no rollover should
        // fire. The line stays on the same calendar day as the login.
        File.WriteAllText(_logPath, "[12:25:03] LocalPlayer: scripted-before-login-stamp\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 12, 25, 3, DateTimeKind.Utc));

        var anchor = new FakeSessionAnchor(new DateTime(2026, 5, 11, 12, 25, 4, DateTimeKind.Utc));
        var reader = new PlayerLogTailReader(_logPath, sessionAnchor: anchor);
        var lines = reader.ReadNew();

        lines.Should().ContainSingle()
            .Which.Timestamp.Should().Be(new DateTime(2026, 5, 11, 12, 25, 3, DateTimeKind.Utc));
    }

    [Fact]
    public void ReadNew_WithoutSessionAnchor_FallsBackToMtimeAnchoring()
    {
        // Same setup as the pre-existing mtime tests, but constructed with
        // sessionAnchor: null explicitly to pin the contract. mtime path
        // remains the fallback for pre-banner lines and the no-session case.
        File.WriteAllText(_logPath, "[14:30:00] LocalPlayer: line\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 14, 30, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath, sessionAnchor: null);
        var lines = reader.ReadNew();

        lines.Should().ContainSingle()
            .Which.Timestamp.Should().Be(new DateTime(2026, 5, 10, 14, 30, 0, DateTimeKind.Utc));
    }

    // ── Seed-marker scenarios ───────────────────────────────────────

    [Fact]
    public void SeedToSessionStart_PrefersEarlierOfBannerAndProcessAddPlayer()
    {
        // Banner emitted first, ProcessAddPlayer a moment later. The seed
        // anchors at the BANNER so GameSessionService can parse it from the
        // replay; ProcessAddPlayer is still in the replay because it comes
        // after.
        //
        // ── LIVE CAPTURE 2026-05-19 ────────────────────────────────────
        // The banner string is the actual line written by the PG client at
        // 2026-05-19 20:01:14 UTC for character Emraell (Player.log). The
        // ProcessAddPlayer in the live log is 4KB of character-appearance
        // payload; truncated here for readability since this test asserts
        // on the substring match, not the full payload (the live
        // ProcessAddPlayer exercise lives in GameSessionServiceTests etc.).
        File.WriteAllText(_logPath,
            "[00:00:00] stale-pre-session-noise\n" +
            "[20:01:14] Logged in as character Emraell. Time UTC=05/19/2026 20:01:14. Timezone Offset 01:00:00\n" +
            "[20:01:17] LocalPlayer: ProcessAddPlayer(-1107394649, 25042203, \"@Base2-m(sex=m;race=e;...\", \"Emraell\", \"A player!\", System.String[], (2550.51, 299.58, 1998.36), (0.00000, 0.96352, 0.00000, -0.26764), Idle, Standing, 0, 0, True)\n" +
            "[20:01:18] LocalPlayer: ProcessSetWeather(\"Clear Sky\", True)\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 19, 20, 1, 18, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        reader.SeedToSessionStart();
        var lines = reader.ReadNew();

        lines.Should().HaveCount(3);
        lines[0].Line.Should().Contain("Logged in as character");
        lines[1].Line.Should().Contain("ProcessAddPlayer");
        lines[2].Line.Should().Contain("ProcessSetWeather");
    }

    [Fact]
    public void SeedToSessionStart_FallsBackToProcessAddPlayerWhenBannerMissing()
    {
        // Pre-banner clients or pathological log states with no banner still
        // anchor on ProcessAddPlayer. Pre-existing behaviour preserved.
        File.WriteAllText(_logPath,
            "[00:00:00] stale-pre-session-noise\n" +
            "[12:25:05] LocalPlayer: ProcessAddPlayer(1, 2, \"\", \"Emraell\")\n" +
            "[12:25:10] LocalPlayer: ProcessCompleteQuest(1, 1)\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 12, 25, 10, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        reader.SeedToSessionStart();
        var lines = reader.ReadNew();

        lines.Should().HaveCount(2);
        lines[0].Line.Should().Contain("ProcessAddPlayer");
        lines[1].Line.Should().Contain("ProcessCompleteQuest");
    }

    // ── #513 scope additions: Sequence + ReadMonotonicTicks ─────────

    [Fact]
    public void ReadNew_Sequence_EqualsByteOffsetOfLineStartInFile()
    {
        // L0 (#513 scope addition A) mints RawLogLine.Sequence as the
        // byte offset of the line's first character in the source file.
        // Restart-stability + within-source monotonicity both flow from
        // this — an L1 high-water filter (#511 deliverable 3) can use
        // Sequence as a restart-stable dedup key precisely because it is
        // derived from log position, not a process counter.
        const string l0 = "[10:00:00] LocalPlayer: line-zero\n";
        const string l1 = "[10:00:01] LocalPlayer: line-one\n";
        const string l2 = "[10:00:02] LocalPlayer: line-two\n";
        File.WriteAllText(_logPath, l0 + l1 + l2);
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 10, 0, 2, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(3);
        lines[0].Sequence.Should().Be(0);
        lines[1].Sequence.Should().Be(l0.Length);
        lines[2].Sequence.Should().Be(l0.Length + l1.Length);
    }

    [Fact]
    public void ReadNew_Sequence_UsesUtf8ByteCountNotCharCount()
    {
        // Sequence is the byte offset of the line's first character in the
        // source file — not the char count, which would mis-key on any
        // line containing a multi-byte UTF-8 character. Player.log content
        // is overwhelmingly ASCII in practice, but the implementation must
        // still account for byte vs char when accumulating the running
        // offset across lines (the L0 hot-path GetByteCount call exists
        // for exactly this case; this test exercises its correctness
        // justification).
        //
        // line-one contains "café" — the 'é' (U+00E9) encodes as two UTF-8
        // bytes (0xC3 0xA9), so line-one's char count differs from its
        // byte count by 1. If the tailer used Length instead of
        // GetByteCount when accumulating, line-two's Sequence would be
        // off-by-one from the true file byte position.
        const string l0 = "[10:00:00] LocalPlayer: line-zero\n";
        const string l1 = "[10:00:01] LocalPlayer: café-one\n";
        const string l2 = "[10:00:02] LocalPlayer: line-two\n";
        File.WriteAllBytes(_logPath, System.Text.Encoding.UTF8.GetBytes(l0 + l1 + l2));
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 10, 0, 2, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(3);
        lines[0].Sequence.Should().Be(0);
        lines[1].Sequence.Should().Be(System.Text.Encoding.UTF8.GetByteCount(l0));
        lines[2].Sequence.Should().Be(
            System.Text.Encoding.UTF8.GetByteCount(l0) + System.Text.Encoding.UTF8.GetByteCount(l1));
        // Belt-and-braces: line-one's byte count is exactly one greater
        // than its char count (the 'é' costs an extra byte).
        System.Text.Encoding.UTF8.GetByteCount(l1).Should().Be(l1.Length + 1);
    }

    [Fact]
    public void ReadNew_Sequence_StrictlyMonotonicAcrossBatches()
    {
        // The second batch's Sequence values continue past where the
        // first batch ended (no reset). This is the "restart-stable"
        // half of the contract: a Mithril restart that re-seeds to a
        // mid-file byte offset N emits subsequent Sequence values
        // starting at N, matching what the pre-restart tailer would
        // have emitted.
        const string l0 = "[10:00:00] LocalPlayer: line-zero\n";
        const string l1 = "[10:00:01] LocalPlayer: line-one\n";
        File.WriteAllText(_logPath, l0 + l1);
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 10, 0, 1, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var first = reader.ReadNew();
        first.Should().HaveCount(2);
        first[1].Sequence.Should().Be(l0.Length);

        const string l2 = "[10:00:02] LocalPlayer: line-two\n";
        File.AppendAllText(_logPath, l2);
        var second = reader.ReadNew();
        second.Should().ContainSingle()
            .Which.Sequence.Should().Be(l0.Length + l1.Length);
    }

    [Fact]
    public void ReadNew_OnTruncation_SequenceResetsAlongsideOffset()
    {
        // Rotation/truncation: fs.Length < _offset resets _offset to 0,
        // so the post-rotation Sequence space restarts at 0. Correct
        // semantics — the consumer's high-water key was scoped to the
        // file identity that no longer exists.
        File.WriteAllText(_logPath, "[10:00:00] LocalPlayer: this is the original long content\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var first = reader.ReadNew();
        first.Should().ContainSingle().Which.Sequence.Should().Be(0);

        // Replacement is strictly shorter than the original so
        // fs.Length < _offset fires the rotation/truncation branch.
        File.WriteAllText(_logPath, "[11:00:00] x\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc));

        var second = reader.ReadNew();
        second.Should().ContainSingle().Which.Sequence.Should().Be(0);
    }

    [Fact]
    public void ReadNew_ReadMonotonicTicks_SharedAcrossBatch_StrictlyAdvancesBetweenBatches()
    {
        // Per-batch (not per-line) granularity is the L0 contract: all
        // lines in one ReadNew share the same tick value. Between two
        // ReadNew calls the tick advances strictly (TimeProvider sampled
        // at distinct instants).
        File.WriteAllText(_logPath,
            "[10:00:00] LocalPlayer: a\n" +
            "[10:00:01] LocalPlayer: b\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 10, 0, 1, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var first = reader.ReadNew();

        first.Should().HaveCount(2);
        first[0].ReadMonotonicTicks.Should().NotBe(0);
        first[1].ReadMonotonicTicks.Should().Be(first[0].ReadMonotonicTicks);

        File.AppendAllText(_logPath, "[10:00:02] LocalPlayer: c\n");
        var second = reader.ReadNew();
        second.Should().ContainSingle();
        second[0].ReadMonotonicTicks.Should().BeGreaterThan(first[0].ReadMonotonicTicks);
    }

    [Fact]
    public void ReadNew_PlayerTimestampOffsetIsZero()
    {
        // Player.log is always UTC; the L0 contract is that PlayerLogClock
        // emits DateTimeOffset with TimeSpan.Zero offset. (The boilerplate
        // removal half of #513: every consumer that today wraps
        // `raw.Timestamp` in `new DateTimeOffset(ts, TimeSpan.Zero)` is
        // wrapping a value that already has the correct offset.)
        File.WriteAllText(_logPath, "[14:30:00] LocalPlayer: line\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 14, 30, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().ContainSingle()
            .Which.Timestamp.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void SeedToSessionStart_WithProcessAddPlayerBeforeBanner_StillStartsAtTheEarlierOne()
    {
        // Defensive: if some PG client variant emits ProcessAddPlayer FIRST
        // and the banner later, picking the earlier marker keeps both in
        // the replay regardless. (Today's expected ordering has the banner
        // first, but the seed is order-agnostic.)
        File.WriteAllText(_logPath,
            "[00:00:00] stale-pre-session-noise\n" +
            "[12:25:03] LocalPlayer: ProcessAddPlayer(1, 2, \"\", \"Emraell\")\n" +
            "[12:25:04] Logged in as character Emraell. Time UTC=05/11/2026 12:25:04. Timezone Offset 01:00:00\n" +
            "[12:25:10] LocalPlayer: ProcessCompleteQuest(1, 1)\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 11, 12, 25, 10, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        reader.SeedToSessionStart();
        var lines = reader.ReadNew();

        lines.Should().HaveCount(3);
        lines[0].Line.Should().Contain("ProcessAddPlayer");
        lines[1].Line.Should().Contain("Logged in as character");
        lines[2].Line.Should().Contain("ProcessCompleteQuest");
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeSessionAnchor : ISessionAnchor
    {
        private DateTime? _loggedInUtc;
        public FakeSessionAnchor(DateTime? loggedInUtc) { _loggedInUtc = loggedInUtc; }
        public DateTime? LoggedInUtc => _loggedInUtc;
        public event EventHandler? AnchorChanged;
        public void Set(DateTime? loggedInUtc)
        {
            _loggedInUtc = loggedInUtc;
            AnchorChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
