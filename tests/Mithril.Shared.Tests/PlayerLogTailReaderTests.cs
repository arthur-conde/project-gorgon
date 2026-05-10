using System.IO;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests;

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
        // against ChatLog's "Timezone Offset" login banner). The sequencer
        // folds the tod over file-mtime-UTC to produce an absolute UTC stamp.
        File.WriteAllText(_logPath,
            "[14:30:00] LocalPlayer: ProcessAddPlayer(1, 2, \"desc\", \"TestChar\")\n" +
            "[15:30:00] LocalPlayer: ProcessCompleteQuest(17415332, 28505)\n");
        File.SetLastWriteTimeUtc(_logPath, new DateTime(2026, 5, 10, 15, 30, 0, DateTimeKind.Utc));

        var reader = new PlayerLogTailReader(_logPath);
        var lines = reader.ReadNew();

        lines.Should().HaveCount(2);
        lines[0].Timestamp.Should().Be(new DateTime(2026, 5, 10, 14, 30, 0, DateTimeKind.Utc));
        lines[1].Timestamp.Should().Be(new DateTime(2026, 5, 10, 15, 30, 0, DateTimeKind.Utc));
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

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTime utc) => _now = new DateTimeOffset(utc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
