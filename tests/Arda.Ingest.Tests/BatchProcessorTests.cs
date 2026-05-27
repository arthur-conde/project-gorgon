using Arda.Abstractions.Logs;
using Arda.Ingest.Classification;
using Arda.Ingest.Clock;
using Arda.Ingest.Coordinator;
using Arda.Ingest.Tailer;
using FluentAssertions;
using Xunit;

namespace Arda.Ingest.Tests;

public class BatchProcessorTests : IDisposable
{
    private readonly string _tempFile;

    public BatchProcessorTests()
    {
        _tempFile = Path.GetTempFileName();
    }

    public void Dispose()
    {
        if (File.Exists(_tempFile))
            File.Delete(_tempFile);
    }

    [Fact]
    public void ProcessBatch_MixedTimestampedAndNoise_ReturnsOnlyClassifiedLines()
    {
        File.WriteAllText(_tempFile,
            "[14:30:05] LocalPlayer: ProcessAddItem(123, -1, False)\n" +
            "LoadAssetAsync: eq-x-m2-head-0. Status=None.\n" +
            "[14:30:06] LocalPlayer: ProcessDeleteItem(456)\n");

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var results = processor.ProcessBatch(tailer, isReplay: false);

        results.Should().NotBeNull();
        results.Should().HaveCount(2);
        results![0].Log.Should().Be("LocalPlayer: ProcessAddItem(123, -1, False)");
        results[1].Log.Should().Be("LocalPlayer: ProcessDeleteItem(456)");
    }

    [Fact]
    public void ProcessBatch_EmptyFile_ReturnsNull()
    {
        File.WriteAllText(_tempFile, "");

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var results = processor.ProcessBatch(tailer, isReplay: false);

        results.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProcessBatch_IsReplayFlag_PassedThroughToMetadata(bool isReplay)
    {
        File.WriteAllText(_tempFile, "[14:30:05] Some game event\n");

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var results = processor.ProcessBatch(tailer, isReplay: isReplay);

        results.Should().NotBeNull();
        results.Should().HaveCount(1);
        results![0].Metadata.IsReplay.Should().Be(isReplay);
    }

    [Fact]
    public void ProcessBatch_ReadOnIsPopulated()
    {
        File.WriteAllText(_tempFile, "[14:30:05] Some game event\n");

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var before = TimeProvider.System.GetUtcNow();
        var results = processor.ProcessBatch(tailer, isReplay: false);
        var after = TimeProvider.System.GetUtcNow();

        results.Should().NotBeNull();
        results![0].Metadata.ReadOn.Should().BeOnOrAfter(before);
        results[0].Metadata.ReadOn.Should().BeOnOrBefore(after);
    }

    [Fact]
    public void ProcessBatch_TimestampedLine_HasTimestampInMetadata()
    {
        File.WriteAllText(_tempFile, "[14:30:05] LocalPlayer: action\n");

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var results = processor.ProcessBatch(tailer, isReplay: false);

        results.Should().NotBeNull();
        results![0].Metadata.Timestamp.Should().NotBeNull();
        results[0].Metadata.Timestamp!.Value.Hour.Should().Be(14);
        results[0].Metadata.Timestamp!.Value.Minute.Should().Be(30);
        results[0].Metadata.Timestamp!.Value.Second.Should().Be(5);
    }

    [Fact]
    public void ProcessBatch_SystemPatternLine_IncludedWithNullTimestamp()
    {
        File.WriteAllText(_tempFile, "Connecting to gameserver port 5555\n");

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var results = processor.ProcessBatch(tailer, isReplay: false);

        results.Should().NotBeNull();
        results.Should().HaveCount(1);
        results![0].Log.Should().Be("Connecting to gameserver port 5555");
        results[0].Metadata.Timestamp.Should().BeNull();
    }

    [Fact]
    public void ProcessBatch_BannerInBatch_AnchorsAgainstBannerNotMtime()
    {
        File.WriteAllText(_tempFile,
            "[14:30:05] Logged in as character Bob. Time UTC=2026-01-15 14:30:05\n" +
            "[14:30:06] LocalPlayer: ProcessAddItem(1)\n");
        // Mtime is wildly different from the banner — banner must win.
        File.SetLastWriteTimeUtc(_tempFile, new DateTime(2027, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var results = processor.ProcessBatch(tailer, isReplay: true);

        results.Should().NotBeNull();
        results.Should().HaveCount(2);
        results![0].Metadata.Timestamp!.Value.Year.Should().Be(2026);
        results[0].Metadata.Timestamp!.Value.Month.Should().Be(1);
        results[0].Metadata.Timestamp!.Value.Day.Should().Be(15);
        results[1].Metadata.Timestamp!.Value.Day.Should().Be(15);
    }

    [Fact]
    public void ProcessBatch_AllNoise_ReturnsEmptyList()
    {
        File.WriteAllText(_tempFile,
            "LoadAssetAsync: eq-x-m2-head-0. Status=None.\n" +
            "Shader warmup: 15 shaders loaded\n");

        var clock = new PlayerLogClock(TimeProvider.System);
        var classifier = new LineClassifier(clock);
        var processor = new BatchProcessor(classifier, TimeProvider.System);
        var tailer = new LogSourceTailer(_tempFile);

        var results = processor.ProcessBatch(tailer, isReplay: false);

        results.Should().NotBeNull();
        results.Should().BeEmpty();
    }
}
