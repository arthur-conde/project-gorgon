using System.IO;
using FluentAssertions;
using Mithril.Shared.Logging;
using Xunit;

namespace Mithril.Shared.Tests;

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
    public void ReadNew_RecoversAbsoluteTimestampFromBracketedPrefix()
    {
        var path = Path.Combine(_dir, "Chat.Global.log");
        File.WriteAllText(path,
            "[14:30:00] [Global] Alice: hi\n" +
            "[15:30:00] [Global] Bob: hey\n");
        var mtime = new DateTime(2026, 5, 10, 15, 30, 0, DateTimeKind.Local);
        File.SetLastWriteTime(path, mtime);

        var reader = new ChatLogTailReader();
        var lines = reader.ReadNew(path);

        lines.Should().HaveCount(2);
        lines[0].Timestamp.ToLocalTime()
            .Should().Be(new DateTime(2026, 5, 10, 14, 30, 0, DateTimeKind.Local));
        lines[1].Timestamp.ToLocalTime()
            .Should().Be(new DateTime(2026, 5, 10, 15, 30, 0, DateTimeKind.Local));
    }

    [Fact]
    public void ReadNew_PerFileSequencerStateIsIndependent()
    {
        // Two channel files with overlapping but non-aligned timestamps —
        // each file's date-folding must be independent. If they shared
        // sequencer state, the second file's earlier timestamp could trip
        // the first file's rollover detector or vice versa.
        var globalPath = Path.Combine(_dir, "Chat.Global.log");
        var partyPath = Path.Combine(_dir, "Chat.Party.log");

        File.WriteAllText(globalPath, "[23:55:00] global pre-midnight\n");
        File.SetLastWriteTime(globalPath,
            new DateTime(2026, 5, 10, 23, 55, 0, DateTimeKind.Local));

        File.WriteAllText(partyPath, "[10:00:00] party morning\n");
        File.SetLastWriteTime(partyPath,
            new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Local));

        var reader = new ChatLogTailReader();
        var globalLines = reader.ReadNew(globalPath);
        var partyLines = reader.ReadNew(partyPath);

        globalLines.Should().ContainSingle()
            .Which.Timestamp.ToLocalTime()
            .Should().Be(new DateTime(2026, 5, 10, 23, 55, 0, DateTimeKind.Local));

        partyLines.Should().ContainSingle()
            .Which.Timestamp.ToLocalTime()
            .Should().Be(new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Local));
    }

    [Fact]
    public void ReadNew_AcrossBatches_PreservesPerFileRolloverState()
    {
        // A single file's first batch ends just before midnight, second batch
        // starts just after. The reader must carry per-file date state across
        // ReadNew calls so the post-midnight line gets the next day.
        var path = Path.Combine(_dir, "Chat.Global.log");
        File.WriteAllText(path, "[23:55:00] pre-midnight\n");
        File.SetLastWriteTime(path,
            new DateTime(2026, 5, 10, 23, 55, 0, DateTimeKind.Local));

        var reader = new ChatLogTailReader();
        var first = reader.ReadNew(path);
        first.Should().ContainSingle()
            .Which.Timestamp.ToLocalTime()
            .Should().Be(new DateTime(2026, 5, 10, 23, 55, 0, DateTimeKind.Local));

        File.AppendAllText(path, "[00:05:00] post-midnight\n");
        File.SetLastWriteTime(path,
            new DateTime(2026, 5, 11, 0, 5, 0, DateTimeKind.Local));

        var second = reader.ReadNew(path);
        second.Should().ContainSingle()
            .Which.Timestamp.ToLocalTime()
            .Should().Be(new DateTime(2026, 5, 11, 0, 5, 0, DateTimeKind.Local));
    }
}
