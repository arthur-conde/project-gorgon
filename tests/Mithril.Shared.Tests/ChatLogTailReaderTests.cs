using System.IO;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests;

/// <summary>
/// Tests for the chat-log half of the L0 layer (#513). Asserts the real
/// chat-log grammar (<c>yy-MM-dd HH:mm:ss\t…</c>, LOCAL time → TZ-correct
/// <see cref="System.DateTimeOffset"/>), Sequence/ReadMonotonicTicks
/// behaviour, and rotation/per-file-state semantics. The pre-#513 tests
/// in this file exercised a synthetic <c>[HH:MM:SS]</c> grammar against
/// the shared sequencer — that grammar was always wrong for chat (real
/// chat lines never carry it) and the L0 redesign splits Player and chat
/// onto separate per-source clocks, so the synthetic-grammar tests no
/// longer make sense and have been replaced.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class ChatLogTailReaderTests : IDisposable
{
    private readonly string _dir;

    public ChatLogTailReaderTests()
    {
        _dir = Mithril.TestSupport.TestPaths.CreateTempDir("mithril-chattailreader");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ReadNew_RealChatPrefix_RoundTripsToHostLocalDateTimeOffset()
    {
        // Real chat-log format: "yy-MM-dd HH:mm:ss\t<content>" in HOST-LOCAL
        // time. L0 parses the prefix and emits a DateTimeOffset whose offset
        // is the local zone's offset for that instant — so consumers see a
        // TZ-correct absolute instant without having to know chat is local.
        var path = Path.Combine(_dir, "Chat.Global.log");
        File.WriteAllText(path,
            "26-04-25 15:10:48\t[Status] Shoddy Phlogiston x5 added to inventory.\n" +
            "26-04-25 15:11:00\t[Global] Alice: hi\n");

        var reader = new ChatLogTailReader();
        var lines = reader.ReadNew(path);

        lines.Should().HaveCount(2);
        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 4, 25, 15, 10, 48));
        lines[0].Timestamp.Should().Be(new DateTimeOffset(2026, 4, 25, 15, 10, 48, localOffset));
        lines[1].Timestamp.Should().Be(new DateTimeOffset(2026, 4, 25, 15, 11, 0, localOffset));
    }

    [Fact]
    public void ReadNew_RealChatPrefix_RegressionGuard_PreservesLocalOffsetNotZero()
    {
        // The #183 regression-class anchor (#513 verification): a chat line
        // must produce the TZ-correct absolute instant — NOT the naive
        // TimeSpan.Zero interpretation that the per-consumer
        // `new DateTimeOffset(raw.Timestamp, TimeSpan.Zero)` wrap was doing
        // before L0 took ownership. If the test host runs in a non-UTC
        // local zone we can assert the offset is not Zero; if it does run
        // at UTC we still assert the offset matches the local zone (which
        // happens to be Zero), so the test is robust to runner TZ.
        var path = Path.Combine(_dir, "Chat.Global.log");
        File.WriteAllText(path, "26-04-25 15:10:48\t[Status] x\n");

        var reader = new ChatLogTailReader();
        var lines = reader.ReadNew(path);

        var localOffset = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 4, 25, 15, 10, 48));
        lines.Should().ContainSingle().Which.Timestamp.Offset.Should().Be(localOffset);
        // And the absolute instant the offset implies is consistent: applying
        // the local offset gets us back to the wall-clock the file recorded.
        lines[0].Timestamp.LocalDateTime.Should().Be(new DateTime(2026, 4, 25, 15, 10, 48));
    }

    [Fact]
    public void ReadNew_UnprefixedLine_InheritsPriorEmittedStamp()
    {
        // Rare in practice (the PG client writes a prefix on every line) but
        // the chat clock must not crash on a stray header or blank-prefix
        // line — it inherits the prior emitted stamp the way the Player-side
        // clock inherits across unprefixed engine noise.
        var path = Path.Combine(_dir, "Chat.Global.log");
        File.WriteAllText(path,
            "26-04-25 15:10:48\t[Status] first line\n" +
            "this line has no prefix at all\n" +
            "26-04-25 15:10:50\t[Status] third line\n");

        var reader = new ChatLogTailReader();
        var lines = reader.ReadNew(path);

        lines.Should().HaveCount(3);
        lines[1].Timestamp.Should().Be(lines[0].Timestamp);  // inherited
        lines[2].Timestamp.LocalDateTime.Should().Be(new DateTime(2026, 4, 25, 15, 10, 50));
    }

    [Fact]
    public void ReadNew_SequenceEqualsByteOffsetInFile()
    {
        // L0 mints RawLogLine.Sequence = byte offset of the line's first
        // character in the source file. Restart-stability + within-source
        // monotonicity both flow from this; the test pins the contract
        // concretely so an L1 high-water filter (#511 deliverable 3) can
        // build on it.
        var path = Path.Combine(_dir, "Chat.Global.log");
        const string l0 = "26-04-25 15:10:48\t[Status] a\n";
        const string l1 = "26-04-25 15:10:49\t[Status] b\n";
        const string l2 = "26-04-25 15:10:50\t[Status] c\n";
        File.WriteAllText(path, l0 + l1 + l2);

        var reader = new ChatLogTailReader();
        var lines = reader.ReadNew(path);

        lines.Should().HaveCount(3);
        lines[0].Sequence.Should().Be(0);
        lines[1].Sequence.Should().Be(l0.Length);
        lines[2].Sequence.Should().Be(l0.Length + l1.Length);
    }

    [Fact]
    public void ReadNew_SequenceContinuesAcrossBatches_RestartStableProxy()
    {
        // First batch ends with two lines; second batch arrives later. The
        // second batch's Sequence values continue at the byte offset where
        // the first batch ended — which is exactly the restart-stable
        // contract: an L1 consumer's persisted "high-water = Sequence N"
        // key continues to filter correctly after a Mithril restart that
        // re-seeds to byte N.
        var path = Path.Combine(_dir, "Chat.Global.log");
        const string l0 = "26-04-25 15:10:48\t[Status] a\n";
        const string l1 = "26-04-25 15:10:49\t[Status] b\n";
        File.WriteAllText(path, l0 + l1);

        var reader = new ChatLogTailReader();
        var first = reader.ReadNew(path);
        first.Should().HaveCount(2);
        first[1].Sequence.Should().Be(l0.Length);

        const string l2 = "26-04-25 15:10:50\t[Status] c\n";
        File.AppendAllText(path, l2);
        var second = reader.ReadNew(path);
        second.Should().ContainSingle()
            .Which.Sequence.Should().Be(l0.Length + l1.Length);
    }

    [Fact]
    public void ReadNew_OnTruncation_SequenceResetsAlongsideOffset()
    {
        // Rotation/truncation: fs.Length < _offset triggers a reset to 0,
        // so the post-rotation Sequence space restarts at 0 too. This is
        // correct — the consumer's high-water key was scoped to the file
        // identity that no longer exists; treating the rotated file as a
        // fresh sequence space is the right semantics, not a bug.
        var path = Path.Combine(_dir, "Chat.Global.log");
        // Long enough that the replacement (below) is strictly shorter,
        // so fs.Length < _offset fires the rotation/truncation branch.
        const string original = "26-04-25 15:10:48\t[Status] this is the original line content\n";
        File.WriteAllText(path, original);

        var reader = new ChatLogTailReader();
        var first = reader.ReadNew(path);
        first.Should().ContainSingle().Which.Sequence.Should().Be(0);

        const string replacement = "26-04-25 16:00:00\tx\n";  // shorter than original
        File.WriteAllText(path, replacement);

        var second = reader.ReadNew(path);
        second.Should().ContainSingle().Which.Sequence.Should().Be(0);
    }

    [Fact]
    public void ReadNew_ReadMonotonicTicks_NonZero_SameWithinBatch()
    {
        // Per-batch (not per-line) granularity is the L0 contract: all
        // lines in one ReadNew call share the same ReadMonotonicTicks
        // value, sampled once via TimeProvider.GetTimestamp. The value
        // itself is process-monotonic; we just assert it's non-zero
        // (something was sampled) and shared across the batch.
        var path = Path.Combine(_dir, "Chat.Global.log");
        File.WriteAllText(path,
            "26-04-25 15:10:48\t[Status] a\n" +
            "26-04-25 15:10:49\t[Status] b\n");

        var reader = new ChatLogTailReader();
        var lines = reader.ReadNew(path);

        lines.Should().HaveCount(2);
        lines[0].ReadMonotonicTicks.Should().NotBe(0);
        lines[1].ReadMonotonicTicks.Should().Be(lines[0].ReadMonotonicTicks);
    }

    [Fact]
    public void ReadNew_PerFileTailerStateIsIndependent()
    {
        // Two channel files with overlapping but unrelated content — each
        // file's tail state (offset, sequence space, clock) must be
        // independent. If they shared state, one file's progress would
        // skew the other's Sequence or stamps.
        var globalPath = Path.Combine(_dir, "Chat.Global.log");
        var partyPath = Path.Combine(_dir, "Chat.Party.log");

        File.WriteAllText(globalPath, "26-04-25 15:10:48\t[Global] hi\n");
        File.WriteAllText(partyPath, "26-04-25 10:00:00\t[Party] hey\n");

        var reader = new ChatLogTailReader();
        var globalLines = reader.ReadNew(globalPath);
        var partyLines = reader.ReadNew(partyPath);

        globalLines.Should().ContainSingle()
            .Which.Timestamp.LocalDateTime.Should().Be(new DateTime(2026, 4, 25, 15, 10, 48));
        partyLines.Should().ContainSingle()
            .Which.Timestamp.LocalDateTime.Should().Be(new DateTime(2026, 4, 25, 10, 0, 0));

        // Both files start at sequence 0 (independent sequence spaces).
        globalLines[0].Sequence.Should().Be(0);
        partyLines[0].Sequence.Should().Be(0);
    }

    [Fact]
    public void ReadNew_AcrossBatches_PreservesPerFileResidualAndOffset()
    {
        // Pre-existing rotation/truncation/per-file-state contract held by
        // the shared tail mechanics — kept as a sanity check after the L0
        // collapse onto LogSourceTailer.
        var path = Path.Combine(_dir, "Chat.Global.log");
        File.WriteAllText(path, "26-04-25 15:10:48\t[Status] first\n");

        var reader = new ChatLogTailReader();
        var first = reader.ReadNew(path);
        first.Should().ContainSingle()
            .Which.Timestamp.LocalDateTime.Should().Be(new DateTime(2026, 4, 25, 15, 10, 48));

        File.AppendAllText(path, "26-04-25 15:10:49\t[Status] second\n");
        var second = reader.ReadNew(path);
        second.Should().ContainSingle()
            .Which.Timestamp.LocalDateTime.Should().Be(new DateTime(2026, 4, 25, 15, 10, 49));
    }
}
