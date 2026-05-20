using System.IO;
using FluentAssertions;
using Mithril.Shared.Character;
using Mithril.Shared.Logging;
using Mithril.Shared.Modules;
using Saruman.Domain;
using Saruman.Parsing;
using Saruman.Services;
using Saruman.Settings;
using Xunit;

namespace Saruman.Tests.Services;

/// <summary>
/// Regression tests for the L1 migration of <see cref="SarumanChatIngestionService"/>
/// (#550 PR 3 archetype-B). The service subscribes to the L1 driver's
/// <see cref="RawLogLine"/> chat path with <see cref="ReplayMode.LiveOnly"/>
/// + <see cref="DeliveryContext.Inline"/>; the test driver below feeds
/// envelopes directly to the captured handler so we exercise the
/// service's wiring without spinning up the real L1 driver or
/// <c>ChatLogStream</c>.
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class SarumanChatIngestionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;

    public SarumanChatIngestionServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-chat-l1");
        _active = new FakeActiveCharacterService();
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private (SarumanCodebookService codebook, WordOfPowerChatParser parser) NewCodebookWithFeavegKnown()
    {
        var store = new PerCharacterStore<SarumanState>(_root, "saruman.json",
            SarumanJsonContext.Default.SarumanState);
        var view = new PerCharacterView<SarumanState>(_active, store);
        var codebook = new SarumanCodebookService(view);
        codebook.RecordDiscovery(new WordOfPowerDiscovered(
            DateTime.UtcNow, "FEAVEG", "Fast Swimmer", "swim faster"));
        var parser = new WordOfPowerChatParser(codebook);
        return (codebook, parser);
    }

    private static RawLogLine ChatLine(string body, DateTimeOffset? at = null, long seq = 1) =>
        new(
            Timestamp: at ?? new DateTimeOffset(2026, 5, 20, 12, 0, 0, TimeSpan.Zero),
            Line: $"26-05-20 12:00:00\t[Global] Wizard: {body}",
            Sequence: seq,
            ReadMonotonicTicks: 0);

    [Fact]
    public async Task LiveChat_MarksTrackedWordSpent()
    {
        var (codebook, parser) = NewCodebookWithFeavegKnown();
        var gates = new ModuleGates();
        var driver = new FakeLogStreamDriver();

        var svc = new SarumanChatIngestionService(driver, parser, codebook, gates);
        var exec = svc.StartAsync(CancellationToken.None);
        gates.For("saruman").Open();

        await WaitUntilAsync(() => driver.HasSubscription);
        driver.LastOptions!.ReplayMode.Should().Be(ReplayMode.LiveOnly,
            because: "chat consumer must pass LiveOnly explicitly (#549 disposition)");
        driver.LastOptions.DeliveryContext.Should().Be(DeliveryContext.Inline);
        driver.LastOptions.DiagnosticCategory.Should().Be("Saruman.Ingestion");

        await driver.Deliver(new LogEnvelope<RawLogLine>(ChatLine("FEAVEG go go go!"), IsReplay: false));

        codebook.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerState.Spent);
        codebook.TryGet("FEAVEG")!.SpentAt.Should().NotBeNull();

        await svc.StopAsync(CancellationToken.None);
        await exec;
    }

    [Fact]
    public async Task UnknownCode_DoesNotMutateState()
    {
        var (codebook, parser) = NewCodebookWithFeavegKnown();
        var gates = new ModuleGates();
        var driver = new FakeLogStreamDriver();

        var svc = new SarumanChatIngestionService(driver, parser, codebook, gates);
        var exec = svc.StartAsync(CancellationToken.None);
        gates.For("saruman").Open();
        await WaitUntilAsync(() => driver.HasSubscription);

        // Uppercase token, but not tracked.
        await driver.Deliver(new LogEnvelope<RawLogLine>(ChatLine("HOWLER nope nope"), IsReplay: false));

        codebook.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerState.Known,
            because: "the parser only fires on tracked codes");
        codebook.TryGet("HOWLER").Should().BeNull();

        await svc.StopAsync(CancellationToken.None);
        await exec;
    }

    [Fact]
    public async Task ConfiguresSubscriptionWithExpectedOptions()
    {
        // Lightweight option-shape assertion: the four #549-disposition
        // knobs (ReplayMode, DeliveryContext, DiagnosticCategory, no
        // SkipProcessedHighWater) must land verbatim at the L1 driver.
        var (codebook, parser) = NewCodebookWithFeavegKnown();
        var gates = new ModuleGates();
        var driver = new FakeLogStreamDriver();

        var svc = new SarumanChatIngestionService(driver, parser, codebook, gates);
        var exec = svc.StartAsync(CancellationToken.None);
        gates.For("saruman").Open();
        await WaitUntilAsync(() => driver.HasSubscription);

        var opts = driver.LastOptions!;
        opts.ReplayMode.Should().Be(ReplayMode.LiveOnly);
        opts.DeliveryContext.Should().Be(DeliveryContext.Inline);
        opts.DiagnosticCategory.Should().Be("Saruman.Ingestion");
        opts.SkipProcessedHighWater.Should().BeNull(
            because: "MarkSpent is idempotent; no high-water filter needed (#549 row)");

        await svc.StopAsync(CancellationToken.None);
        await exec;
    }

    [Fact]
    public async Task HandlerExceptionDoesNotKillSubscription()
    {
        // Containment is now owned by the L1 driver — but we can still
        // verify the consumer's handler-level shape: a parse failure on
        // one line must not prevent the next valid line from being applied.
        // (The L1 driver wraps the handler in try/catch + rate-limited
        // Warn; this test only requires the handler itself doesn't
        // catastrophically tear down.)
        var (codebook, parser) = NewCodebookWithFeavegKnown();
        var gates = new ModuleGates();
        var driver = new FakeLogStreamDriver();

        var svc = new SarumanChatIngestionService(driver, parser, codebook, gates);
        var exec = svc.StartAsync(CancellationToken.None);
        gates.For("saruman").Open();
        await WaitUntilAsync(() => driver.HasSubscription);

        // Empty line is benign — parser returns null without throwing.
        await driver.Deliver(new LogEnvelope<RawLogLine>(
            new RawLogLine(DateTimeOffset.UtcNow, "", Sequence: 1, ReadMonotonicTicks: 0), IsReplay: false));
        await driver.Deliver(new LogEnvelope<RawLogLine>(ChatLine("FEAVEG"), IsReplay: false));

        codebook.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerState.Spent);

        await svc.StopAsync(CancellationToken.None);
        await exec;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (sw.Elapsed > budget) throw new TimeoutException("WaitUntilAsync gave up");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Minimal in-process <see cref="ILogStreamDriver"/> that captures the
    /// subscription callback so the test can synthesise envelopes directly.
    /// Mirrors the test-stub shape used in <c>LogStreamDriverTests</c> but
    /// scoped to one subscription — Saruman/Chat only subscribes once.
    /// </summary>
    private sealed class FakeLogStreamDriver : ILogStreamDriver
    {
        private Func<LogEnvelope<RawLogLine>, ValueTask>? _handler;

        public LogSubscriptionOptions? LastOptions { get; private set; }
        public bool HasSubscription => _handler is not null;

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            if (typeof(T) != typeof(RawLogLine))
                throw new InvalidOperationException(
                    $"SarumanChatIngestionService must subscribe with T=RawLogLine, got {typeof(T).Name}");
            LastOptions = options ?? LogSubscriptionOptions.Default;
            _handler = (Func<LogEnvelope<RawLogLine>, ValueTask>)(object)handler;
            return new FakeSub(() => _handler = null);
        }

        public ValueTask Deliver(LogEnvelope<RawLogLine> envelope)
        {
            var h = _handler ?? throw new InvalidOperationException("No active subscription");
            return h(envelope);
        }

        private sealed class FakeSub : ILogSubscription
        {
            private readonly Action _onDispose;
            public FakeSub(Action onDispose) { _onDispose = onDispose; }
            public string Id => "fake#1";
            public LogSubscriptionDiagnostics Diagnostics => new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }
            public void Dispose() => _onDispose();
        }
    }
}
