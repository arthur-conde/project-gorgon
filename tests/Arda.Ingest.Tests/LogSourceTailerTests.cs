using System.Text;
using Arda.Ingest.Internal;
using Arda.Ingest.Tailer;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public class LogSourceTailerTests : IDisposable
{
    private readonly string _tempFile;

    public LogSourceTailerTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    [Fact]
    public void ReadNew_MultipleCompleteLines_ReturnsCorrectLineBoundaries()
    {
        File.WriteAllText(_tempFile, "[14:30:05] Line one\n[14:30:06] Line two\n[14:30:07] Line three\n");
        var tailer = new LogSourceTailer(_tempFile);

        var batch = tailer.ReadNew();
        try
        {
            batch.IsEmpty.Should().BeFalse();
            batch.LineCount.Should().Be(3);
            GetLine(batch, 0).Should().Be("[14:30:05] Line one");
            GetLine(batch, 1).Should().Be("[14:30:06] Line two");
            GetLine(batch, 2).Should().Be("[14:30:07] Line three");
        }
        finally
        {
            batch.Dispose();
        }
    }

    [Fact]
    public void ReadNew_FileGrowsBetweenCalls_ReturnsOnlyNewLines()
    {
        File.WriteAllText(_tempFile, "[14:30:05] First line\n");
        var tailer = new LogSourceTailer(_tempFile);

        var batch1 = tailer.ReadNew();
        try
        {
            batch1.LineCount.Should().Be(1);
            GetLine(batch1, 0).Should().Be("[14:30:05] First line");
        }
        finally
        {
            batch1.Dispose();
        }

        File.AppendAllText(_tempFile, "[14:30:06] Second line\n[14:30:07] Third line\n");

        var batch2 = tailer.ReadNew();
        try
        {
            batch2.LineCount.Should().Be(2);
            GetLine(batch2, 0).Should().Be("[14:30:06] Second line");
            GetLine(batch2, 1).Should().Be("[14:30:07] Third line");
        }
        finally
        {
            batch2.Dispose();
        }
    }

    [Fact]
    public void ReadNew_EmptyFile_ReturnsEmptyBatch()
    {
        File.WriteAllText(_tempFile, "");
        var tailer = new LogSourceTailer(_tempFile);

        var batch = tailer.ReadNew();
        batch.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ReadNew_FileDoesNotExist_ReturnsEmptyBatch()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".log");
        var tailer = new LogSourceTailer(nonExistentPath);

        var batch = tailer.ReadNew();
        batch.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ReadNew_FileTruncated_ResetsAndReReads()
    {
        File.WriteAllText(_tempFile, "[14:30:05] First line\n[14:30:06] Second line\n");
        var tailer = new LogSourceTailer(_tempFile);

        var batch1 = tailer.ReadNew();
        try
        {
            batch1.LineCount.Should().Be(2);
        }
        finally
        {
            batch1.Dispose();
        }

        File.WriteAllText(_tempFile, "[14:30:07] New\n");

        var batch2 = tailer.ReadNew();
        try
        {
            batch2.LineCount.Should().Be(1);
            GetLine(batch2, 0).Should().Be("[14:30:07] New");
        }
        finally
        {
            batch2.Dispose();
        }
    }

    [Fact]
    public void ReadNew_PartialLine_BufferedAsResidualUntilNewlineArrives()
    {
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("partial line no newline"));
        var tailer = new LogSourceTailer(_tempFile);

        var batch1 = tailer.ReadNew();
        batch1.IsEmpty.Should().BeTrue("partial line without newline should be buffered");

        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes("partial line no newline\n"));

        var batch2 = tailer.ReadNew();
        try
        {
            batch2.IsEmpty.Should().BeFalse();
            batch2.LineCount.Should().Be(1);
            GetLine(batch2, 0).Should().Be("partial line no newline");
        }
        finally
        {
            batch2.Dispose();
        }
    }

    [Fact]
    public void HasCaughtUp_TrueAfterReadingToEof()
    {
        File.WriteAllText(_tempFile, "[14:30:05] Line one\n");
        var tailer = new LogSourceTailer(_tempFile);

        tailer.HasCaughtUp.Should().BeFalse();

        var batch = tailer.ReadNew();
        try
        {
            tailer.HasCaughtUp.Should().BeTrue();
        }
        finally
        {
            batch.Dispose();
        }
    }

    [Fact]
    public void ReadNew_CrlfLineEndings_StrippedToJustContent()
    {
        File.WriteAllBytes(_tempFile, Encoding.UTF8.GetBytes(
            "[14:30:05] Line one\r\n[14:30:06] Line two\r\n"));
        var tailer = new LogSourceTailer(_tempFile);

        var batch = tailer.ReadNew();
        try
        {
            batch.LineCount.Should().Be(2);
            GetLine(batch, 0).Should().Be("[14:30:05] Line one");
            GetLine(batch, 1).Should().Be("[14:30:06] Line two");
            GetLine(batch, 0).Should().NotContain("\r");
        }
        finally
        {
            batch.Dispose();
        }
    }

    [Fact]
    public void ReadNew_NoNewContent_ReturnsEmptyBatchAfterCatchUp()
    {
        File.WriteAllText(_tempFile, "[14:30:05] Line\n");
        var tailer = new LogSourceTailer(_tempFile);

        var batch1 = tailer.ReadNew();
        batch1.Dispose();

        var batch2 = tailer.ReadNew();
        batch2.IsEmpty.Should().BeTrue();
        tailer.HasCaughtUp.Should().BeTrue();
    }

    private static string GetLine(TailedBatch batch, int index)
    {
        var (start, length) = batch.Lines[index];
        return new string(batch.Buffer, start, length);
    }
}
