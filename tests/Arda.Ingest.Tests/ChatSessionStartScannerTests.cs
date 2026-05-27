using Arda.Ingest.Coordinator;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public sealed class ChatSessionStartScannerTests
{
    [Fact]
    public void TryFindLastBannerLineStart_ReturnsLastBannerLine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chat-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllText(path,
                """
                26-05-26 10:00:00	[Local] hello
                26-05-26 11:00:00	**** Logged In As Alpha. Server One. Timezone Offset 01:00:00.
                26-05-26 12:00:00	[Local] between
                26-05-26 13:00:00	**** Logged In As Beta. Server Two. Timezone Offset 02:00:00.
                """);

            ChatSessionStartScanner.TryFindLastBannerLineStart(path, out var offset).Should().BeTrue();
            using var fs = File.OpenRead(path);
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var line = reader.ReadLine();
            line.Should().Contain("Logged In As Beta");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ResolveSessionStart_SkipsFilesBeforeNewestBanner()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"chatlogs-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Chat-26-05-25.log"),
                "26-05-25 09:00:00	**** Logged In As Old. Server One. Timezone Offset 00:00:00.\n");
            File.WriteAllText(Path.Combine(dir, "Chat-26-05-26.log"),
                "26-05-26 09:00:00	**** Logged In As New. Server One. Timezone Offset 00:00:00.\n");

            var files = Directory.GetFiles(dir, "Chat-??-??-??.log").Order().ToArray();
            var (index, offset) = ChatSessionStartScanner.ResolveSessionStart(files);

            index.Should().Be(1);
            offset.Should().Be(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
