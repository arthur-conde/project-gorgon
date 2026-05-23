using System.IO;
using System.Text;
using FluentAssertions;
using Mithril.GameState.Areas;
using Mithril.GameState.Areas.Parsing;
using Mithril.GameState.Areas.Producers;
using Mithril.GameState.Tests.TestSupport;
using Mithril.Shared.Game;
using Mithril.Shared.Logging;
using Mithril.WorldSim;
using Xunit;

namespace Mithril.GameState.Tests.Areas.Producers;

/// <summary>
/// Unit tests for <see cref="AreaLoadingFrameProducer"/> — #775. Covers two
/// surfaces:
/// <list type="bullet">
///   <item><see cref="AreaLoadingFrameProducer.TryBuildSeedFrame"/> — pure
///   reverse-scan helper. The hosted-service registration calls this
///   synchronously at its own <c>StartAsync</c> to pre-warm the folder
///   before downstream consumers' synchronous-read paths run.</item>
///   <item><see cref="AreaLoadingFrameProducer.SubscribeAsync"/> — L1
///   SystemSignal forwarding + mode flip. Pure-L1: no seed frame is
///   yielded through the async enumerator (the eager pre-warm path handles
///   that).</item>
/// </list>
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class AreaLoadingFrameProducerTests : IDisposable
{
    private readonly string _tempDir;

    public AreaLoadingFrameProducerTests()
    {
        _tempDir = Mithril.TestSupport.TestPaths.CreateTempDir("area_loading_producer");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── TryBuildSeedFrame: pure reverse-scan helper ────────────────────────

    [Fact]
    public void Seed_picks_the_LATEST_LOADING_LEVEL_with_its_parsed_log_timestamp()
    {
        // Fixture with two LOADING LEVEL lines; the seed must come from the
        // LATER line (the reverse-scan returns the last marker before EOF)
        // and be stamped with that line's parsed [HH:MM:SS] anchored to the
        // file's mtime. The earlier line is ignored.
        var fileMtime = new DateTime(2026, 5, 23, 18, 30, 0, DateTimeKind.Utc);
        var path = WriteLog(fileMtime,
            "[17:11:10] LOADING LEVEL AreaSerbule",
            "[17:11:11] LocalPlayer: ProcessAddPlayer(...)",
            "[18:28:06] LOADING LEVEL AreaEltibule",
            "[18:28:07] LocalPlayer: ProcessAddPlayer(...)",
            "[18:30:00] LocalPlayer: ProcessNewPosition(0, 0, 0)");

        using var driver = new TestLogStreamDriver();
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(),
            new GameConfig { GameRoot = _tempDir });

        var seed = producer.TryBuildSeedFrame();

        seed.Should().NotBeNull();
        seed!.Value.Payload.AreaKey.Should().Be("AreaEltibule");
        seed.Value.Timestamp.Should().Be(
            new DateTimeOffset(2026, 5, 23, 18, 28, 6, TimeSpan.Zero),
            because: "the seed must stamp the LATER line's parsed [HH:MM:SS], not wall-clock");
    }

    [Fact]
    public void Seed_handles_ChooseCharacter_as_null_area()
    {
        var fileMtime = new DateTime(2026, 5, 23, 19, 0, 0, DateTimeKind.Utc);
        WriteLog(fileMtime,
            "[17:11:10] LOADING LEVEL AreaSerbule",
            "[18:30:35] LOADING LEVEL ChooseCharacter");

        using var driver = new TestLogStreamDriver();
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(),
            new GameConfig { GameRoot = _tempDir });

        var seed = producer.TryBuildSeedFrame();

        seed.Should().NotBeNull();
        seed!.Value.Payload.AreaKey.Should().BeNull();
    }

    [Fact]
    public void Seed_is_null_when_no_GameConfig()
    {
        using var driver = new TestLogStreamDriver();
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(), config: null);

        producer.TryBuildSeedFrame().Should().BeNull();
    }

    [Fact]
    public void Seed_is_null_when_no_log_file()
    {
        using var driver = new TestLogStreamDriver();
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(),
            new GameConfig { GameRoot = Path.Combine(_tempDir, "no-such-dir") });

        producer.TryBuildSeedFrame().Should().BeNull();
    }

    [Fact]
    public void Seed_is_null_when_no_LOADING_LEVEL_marker()
    {
        var fileMtime = new DateTime(2026, 5, 23, 19, 0, 0, DateTimeKind.Utc);
        WriteLog(fileMtime,
            "[18:00:00] LocalPlayer: ProcessAddItem(Apple(1), -1, True)",
            "[18:30:00] LocalPlayer: ProcessAddPlayer(...)");

        using var driver = new TestLogStreamDriver();
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(),
            new GameConfig { GameRoot = _tempDir });

        producer.TryBuildSeedFrame().Should().BeNull();
    }

    // ── SubscribeAsync: L1 forwarding (no seed in the stream) ──────────────

    [Fact]
    public async Task SubscribeAsync_does_NOT_yield_a_seed_frame()
    {
        // Even with a valid Player.log on disk, the seed comes through the
        // pre-warm path, NOT the async enumerator. Pushing zero L1
        // envelopes => zero yielded frames, even when TryBuildSeedFrame()
        // would have produced one.
        var fileMtime = new DateTime(2026, 5, 23, 18, 30, 0, DateTimeKind.Utc);
        WriteLog(fileMtime, "[17:11:10] LOADING LEVEL AreaSerbule");

        using var driver = new TestLogStreamDriver();
        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(),
            new GameConfig { GameRoot = _tempDir });

        var frames = await CollectAvailableAsync(producer, expectedCount: 0);

        frames.Should().BeEmpty();
    }

    [Fact]
    public async Task Forwards_AreaLoading_envelopes_as_frames()
    {
        using var driver = new TestLogStreamDriver();
        var ts1 = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 5, 23, 14, 35, 0, TimeSpan.Zero);
        driver.PushReplay(MakeAreaLoading("AreaSerbule", ts1, seq: 1));
        driver.PushReplay(MakeAreaLoading("AreaEltibule", ts2, seq: 2));

        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(), config: null);

        var frames = await CollectAvailableAsync(producer, expectedCount: 2);

        frames.Should().HaveCount(2);
        frames[0].Payload.AreaKey.Should().Be("AreaSerbule");
        frames[0].Timestamp.Should().Be(ts1);
        frames[1].Payload.AreaKey.Should().Be("AreaEltibule");
        frames[1].Timestamp.Should().Be(ts2);
    }

    [Fact]
    public async Task Ignores_non_AreaLoading_SystemSignal_kinds()
    {
        using var driver = new TestLogStreamDriver();
        var ts = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        driver.PushReplay(MakeSignal(SystemSignalKind.LoginBanner,
            "Logged in as character X. Time UTC=...", ts, seq: 1));
        driver.PushReplay(MakeSignal(SystemSignalKind.PlayerAdded,
            "ProcessAddPlayer(...)", ts, seq: 2));
        driver.PushReplay(MakeSignal(SystemSignalKind.SessionLifecycle,
            "loginCharacter", ts, seq: 3));
        driver.PushReplay(MakeAreaLoading("AreaSerbule", ts, seq: 4));

        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(), config: null);

        var frames = await CollectAvailableAsync(producer, expectedCount: 1);

        frames.Should().ContainSingle()
            .Which.Payload.AreaKey.Should().Be("AreaSerbule");
    }

    // ── Mode awareness ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReachedLive_flips_on_first_non_replay_envelope_regardless_of_kind()
    {
        using var driver = new TestLogStreamDriver();
        var ts = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        // Live LoginBanner — not an AreaLoading frame but still drives the
        // mode flip per the producer's class-doc rationale.
        driver.PushLive(MakeSignal(SystemSignalKind.LoginBanner,
            "Logged in as character X.", ts, seq: 1));

        var producer = new AreaLoadingFrameProducer(
            driver, new AreaTransitionParser(), config: null);

        producer.ReachedLive.IsCompleted.Should().BeFalse();

        // IAsyncEnumerable lazy-evaluates: the iterator body (which opens
        // the L1 subscription) doesn't run until MoveNextAsync is invoked.
        // Calling MoveNextAsync explicitly drives the iterator body
        // synchronously through _driver.Subscribe, starting the L1 pump
        // before we proceed. Mirrors how PlayerWorld's merger primes each
        // producer at registration.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var iter = producer.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var pending = iter.MoveNextAsync();
        try
        {
            await driver.DrainSystemAsync();
            await producer.ReachedLive.WaitAsync(TimeSpan.FromSeconds(2));
            producer.ReachedLive.IsCompleted.Should().BeTrue();
        }
        finally
        {
            cts.Cancel();
            try { _ = await pending; } catch (OperationCanceledException) { /* expected */ }
            try { await iter.DisposeAsync(); } catch (OperationCanceledException) { /* expected */ }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private string WriteLog(DateTime mtimeUtc, params string[] lines)
    {
        var path = Path.Combine(_tempDir, "Player.log");
        File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, mtimeUtc);
        return path;
    }

    private static SystemSignalLogLine MakeAreaLoading(string area, DateTimeOffset ts, long seq) =>
        new(Timestamp: ts, Kind: SystemSignalKind.AreaLoading,
            Data: $"LOADING LEVEL {area}", Sequence: seq, ReadMonotonicTicks: 0);

    private static SystemSignalLogLine MakeSignal(
        SystemSignalKind kind, string data, DateTimeOffset ts, long seq) =>
        new(Timestamp: ts, Kind: kind, Data: data, Sequence: seq, ReadMonotonicTicks: 0);

    private static async Task<List<Frame<AreaLoadingFrame>>> CollectAvailableAsync(
        AreaLoadingFrameProducer producer, int expectedCount = 0)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frames = new List<Frame<AreaLoadingFrame>>();
        var collectionTask = Task.Run(async () =>
        {
            await foreach (var f in producer.SubscribeAsync(cts.Token).ConfigureAwait(false))
            {
                frames.Add(f);
                if (expectedCount > 0 && frames.Count >= expectedCount) break;
            }
        }, cts.Token);

        if (expectedCount == 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));
            cts.Cancel();
            try { await collectionTask; } catch (OperationCanceledException) { }
            return frames;
        }

        try { await collectionTask; }
        catch (OperationCanceledException) { /* surfaced via the asserts */ }
        return frames;
    }
}
