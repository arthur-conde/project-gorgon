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
/// L1-migration tests for <see cref="SarumanDiscoveryIngestionService"/>
/// (#550 PR 3 archetype-B). The service subscribes to the L1 driver's
/// <see cref="LocalPlayerLogLine"/> pipe with <see cref="ReplayMode.SinceSubscribe"/>
/// + <see cref="DeliveryContext.Inline"/> + a persisted high-water
/// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>; the test
/// driver below captures the handler so the test can synthesise envelopes
/// directly. Mirrors the <c>SarumanChatIngestionServiceTests</c> stub
/// pattern (#557 / PR #556 review).
///
/// <para>Two shapes covered:</para>
/// <list type="bullet">
///   <item><b>Subscription-options shape</b> — assert the four #549
///   disposition knobs (ReplayMode, DeliveryContext, DiagnosticCategory,
///   SkipProcessedHighWater) land verbatim at the L1 driver. SinceSubscribe
///   semantics + the high-water gate live INSIDE the driver — that's
///   tested in <c>LogStreamDriverTests</c>. These consumer-side tests
///   assert the CONFIG the consumer hands to the driver, not the driver's
///   runtime filtering.</item>
///   <item><b>Byte-equivalence under restart</b> — feed N envelopes via the
///   captured handler, snapshot state, dispose; instantiate a fresh service
///   with the same on-disk state, confirm it passes the persisted
///   high-water to the driver as <c>SkipProcessedHighWater</c>. The
///   per-character state file is what makes the gate survive restart, and
///   that's what this test pins.</item>
/// </list>
///
/// <para>First archetype-B consumer to land high-water persistence — the
/// shape here is the template for the parallel Pippin / Samwise / Legolas
/// migrations.</para>
/// </summary>
[Trait("Category", "FileIO")]
[Collection("FileIO")]
public sealed class SarumanDiscoveryIngestionServiceTests : IDisposable
{
    private readonly string _root;
    private readonly FakeActiveCharacterService _active;

    public SarumanDiscoveryIngestionServiceTests()
    {
        _root = Mithril.TestSupport.TestPaths.CreateTempDir("saruman-discovery-l1");
        _active = new FakeActiveCharacterService();
        _active.SetActiveCharacter("Arthur", "Kwatoxi");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private const string DiscoveryLineZockzech =
        "ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>ZOCKZECH</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Anemia</size></b>\\nSpeaking this word weakens your body so that all attacks cost +5 Power. Lasts 5 minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")";

    private const string DiscoveryLineFeaveg =
        "ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>FEAVEG</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Fast Swimmer</size></b>\\nSpeaking this word makes you swim much faster for five minutes.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")";

    private const string DiscoveryLineTeckplue =
        "ProcessBook(\"You discovered a word of power!\", \"You've discovered a word of power: <sel>TECKPLUE</sel>Speak it out loud for this effect:\\n\\n<b><size=125%>Word of Power: Cure Bovinity</size></b>\\nSpeaking this word causes you to stop being a cow if you are one.\\n\\n<i><size=75%>Words of Power can only be spoken once, then they lose their power. Write this Word down, so you can use it later.\\n\\nYou can only benefit from the effects of two Words of Power simultaneously.</size></i>\", \"\", \"\", \"\", True, False, False, False, False, \"\")";

    [Fact]
    public async Task ConfiguresSubscriptionWithExpectedOptions()
    {
        // Asserts the four #549-disposition knobs land verbatim at the
        // L1 driver: SinceSubscribe + Inline + persisted high-water (null
        // on first cold start, when no discovery has yet been recorded)
        // + DiagnosticCategory "Saruman.Ingestion".
        var (codebook, view, _) = NewCodebookEmpty();
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await StartServiceAsync(driver, codebook);

        var opts = driver.LastOptions!;
        opts.ReplayMode.Should().Be(ReplayMode.SinceSubscribe,
            because: "lazy module — only live discoveries matter; FromSessionStart would re-inflate DiscoveryCount every gate-open (#549 row)");
        opts.DeliveryContext.Should().Be(DeliveryContext.Inline,
            because: "VM self-dispatches on CodebookChanged; no need for driver-side dispatcher hop");
        opts.DiagnosticCategory.Should().Be("Saruman.Ingestion",
            because: "preserves the pre-L1 ThrottledWarn category so log consumers see no churn");
        opts.SkipProcessedHighWater.Should().BeNull(
            because: "no discovery has been recorded yet — null is the documented \"no filter\" signal");

        await StopAsync(svc, exec);
        view.Dispose();
    }

    [Fact]
    public async Task Live_discovery_advances_codebook_and_highwater()
    {
        var (codebook, view, _) = NewCodebookEmpty();
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await StartServiceAsync(driver, codebook);

        await driver.Deliver(MakeEnvelope(DiscoveryLineZockzech, sequence: 4242));

        codebook.IsTracked("ZOCKZECH").Should().BeTrue();
        codebook.TryGet("ZOCKZECH")!.DiscoveryCount.Should().Be(1);
        codebook.DiscoveryHighWaterSequence.Should().Be(4242,
            because: "the ingestion service must advance the persisted high-water to the envelope's Sequence");

        await StopAsync(svc, exec);
        view.Dispose();
    }

    [Fact]
    public async Task Highwater_is_max_of_seen_sequences_not_last_seen()
    {
        var (codebook, view, _) = NewCodebookEmpty();
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await StartServiceAsync(driver, codebook);

        // Out-of-order sequences (the production driver never delivers
        // these out of order, but the max-clamp invariant lives in the
        // codebook layer — defend it explicitly).
        await driver.Deliver(MakeEnvelope(DiscoveryLineFeaveg, sequence: 9000));
        await driver.Deliver(MakeEnvelope(DiscoveryLineTeckplue, sequence: 1500));

        codebook.DiscoveryHighWaterSequence.Should().Be(9000,
            because: "max-clamp invariant: a smaller sequence must not regress the high-water");

        await StopAsync(svc, exec);
        view.Dispose();
    }

    /// <summary>
    /// Byte-equivalence regression: feed N envelopes, snapshot the codebook
    /// + persisted high-water, dispose; on a fresh service the second
    /// subscription must pass the persisted high-water back to the driver
    /// as <c>SkipProcessedHighWater</c>. The driver's gate then drops every
    /// already-seen envelope before the handler runs — preventing
    /// <see cref="KnownWord.DiscoveryCount"/> re-inflation, preserving the
    /// <see cref="WordOfPowerState.Spent"/> flag, never resurrecting an
    /// entry. The handler-side correctness (high-water as the gate) is
    /// exercised in <c>LogStreamDriverTests</c>; this test pins the
    /// CONSUMER-side wiring that makes the gate survive a Mithril restart.
    /// </summary>
    [Fact]
    public async Task Restart_persists_highwater_and_passes_it_back_to_driver()
    {
        var store = NewStore();

        // === Pass 1: cold start ===
        long? firstPassHighWater;
        Dictionary<string, KnownWord> firstPass;
        {
            var view = new PerCharacterView<SarumanState>(_active, store);
            var codebook = new SarumanCodebookService(view);
            var driver = new FakeLogStreamDriver();
            var (svc, exec) = await StartServiceAsync(driver, codebook);

            driver.LastOptions!.SkipProcessedHighWater.Should().BeNull(
                because: "first cold start — no persisted high-water yet");

            await driver.Deliver(MakeEnvelope(DiscoveryLineZockzech, sequence: 100));
            await driver.Deliver(MakeEnvelope(DiscoveryLineFeaveg, sequence: 200));
            await driver.Deliver(MakeEnvelope(DiscoveryLineTeckplue, sequence: 300));

            // Spend one — the subtlest re-inflation risk is that a bare
            // DiscoveryCount++ on replay also clears Spent → Known, which
            // would silently destroy the user's "this word's been used"
            // record across a restart.
            codebook.MarkSpent("FEAVEG", DateTime.UtcNow).Should().BeTrue();

            firstPassHighWater = codebook.DiscoveryHighWaterSequence;
            firstPass = SnapshotCodebook(codebook);

            await StopAsync(svc, exec);
            view.Dispose();
        }

        firstPassHighWater.Should().Be(300);
        firstPass.Should().HaveCount(3);
        firstPass["FEAVEG"].State.Should().Be(WordOfPowerState.Spent);

        // === Pass 2: simulated Mithril restart — same on-disk state ===
        {
            var view = new PerCharacterView<SarumanState>(_active, store);
            var codebook = new SarumanCodebookService(view);

            codebook.DiscoveryHighWaterSequence.Should().Be(300,
                because: "the persisted high-water survives a Mithril restart and is what feeds the driver's SkipProcessedHighWater");
            // The codebook contents themselves must round-trip the Spent flag.
            codebook.TryGet("FEAVEG")!.State.Should().Be(WordOfPowerState.Spent);

            var driver = new FakeLogStreamDriver();
            var (svc, exec) = await StartServiceAsync(driver, codebook);

            driver.LastOptions!.SkipProcessedHighWater.Should().Be(300,
                because: "on restart, the ingestion service reads the persisted high-water and hands it to the L1 driver as SkipProcessedHighWater — the driver's gate then drops every replayed/redelivered envelope with seq <= 300 before our handler runs, preventing DiscoveryCount re-inflation");

            // A NEW envelope at seq 400 > the high-water DOES get
            // processed — proving the filter isn't over-aggressive
            // (a follow-up live discovery after a restart still works).
            await driver.Deliver(MakeEnvelope(DiscoveryLineZockzech, sequence: 400));
            codebook.TryGet("ZOCKZECH")!.DiscoveryCount.Should().Be(2,
                because: "a new envelope above the high-water IS processed; the persistence is a filter not a kill-switch");
            codebook.DiscoveryHighWaterSequence.Should().Be(400);

            await StopAsync(svc, exec);
            view.Dispose();
        }
    }

    [Fact]
    public async Task Restart_with_no_recorded_discovery_passes_null_highwater()
    {
        var store = NewStore();
        var view = new PerCharacterView<SarumanState>(_active, store);
        var codebook = new SarumanCodebookService(view);
        var driver = new FakeLogStreamDriver();
        var (svc, exec) = await StartServiceAsync(driver, codebook);

        driver.LastOptions!.SkipProcessedHighWater.Should().BeNull(
            because: "DiscoveryHighWaterSequence is null until the first discovery is recorded; the ingestion service must propagate null rather than coerce to 0 (which would still filter seq=0)");

        await StopAsync(svc, exec);
        view.Dispose();
    }

    // === Helpers ===

    private (SarumanCodebookService codebook, PerCharacterView<SarumanState> view, PerCharacterStore<SarumanState> store) NewCodebookEmpty()
    {
        var store = NewStore();
        var view = new PerCharacterView<SarumanState>(_active, store);
        return (new SarumanCodebookService(view), view, store);
    }

    private PerCharacterStore<SarumanState> NewStore() =>
        new(_root, "saruman.json", SarumanJsonContext.Default.SarumanState);

    private async Task<(SarumanDiscoveryIngestionService svc, Task exec)> StartServiceAsync(
        FakeLogStreamDriver driver, SarumanCodebookService codebook)
    {
        var gates = new ModuleGates();
        var svc = new SarumanDiscoveryIngestionService(
            driver, new WordOfPowerDiscoveredParser(), codebook, gates);
        var exec = svc.StartAsync(CancellationToken.None);
        gates.For("saruman").Open();
        await WaitUntilAsync(() => driver.HasSubscription);
        return (svc, exec);
    }

    private static async Task StopAsync(SarumanDiscoveryIngestionService svc, Task exec)
    {
        await svc.StopAsync(CancellationToken.None);
        await exec;
    }

    private static LogEnvelope<LocalPlayerLogLine> MakeEnvelope(string data, long sequence) =>
        new(new LocalPlayerLogLine(
                Timestamp: DateTimeOffset.UtcNow,
                Data: data,
                Sequence: sequence,
                ReadMonotonicTicks: 0),
            IsReplay: false);

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

    private static Dictionary<string, KnownWord> SnapshotCodebook(SarumanCodebookService codebook)
    {
        var dict = new Dictionary<string, KnownWord>(StringComparer.Ordinal);
        foreach (var w in codebook.Words)
        {
            dict[w.Code] = new KnownWord
            {
                Code = w.Code,
                EffectName = w.EffectName,
                Description = w.Description,
                FirstDiscoveredAt = w.FirstDiscoveredAt,
                DiscoveryCount = w.DiscoveryCount,
                State = w.State,
                SpentAt = w.SpentAt,
            };
        }
        return dict;
    }

    /// <summary>
    /// Minimal in-process <see cref="ILogStreamDriver"/> that captures the
    /// subscription callback so the test can synthesise envelopes directly.
    /// Mirrors the stub in <c>SarumanChatIngestionServiceTests</c> (which
    /// itself follows the <c>LogStreamDriverTests</c> shape) — scoped to
    /// one subscription because Saruman/Discovery only subscribes once.
    ///
    /// <para>Intentionally does NOT replicate the driver's
    /// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/> gate;
    /// the driver's filter behaviour is exercised in
    /// <c>LogStreamDriverTests</c>. These consumer-side tests assert the
    /// CONFIG the consumer hands to the driver, not the driver's runtime
    /// filtering.</para>
    /// </summary>
    private sealed class FakeLogStreamDriver : ILogStreamDriver
    {
        private Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>? _handler;

        public LogSubscriptionOptions? LastOptions { get; private set; }
        public bool HasSubscription => _handler is not null;

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            if (typeof(T) != typeof(LocalPlayerLogLine))
                throw new InvalidOperationException(
                    $"SarumanDiscoveryIngestionService must subscribe with T=LocalPlayerLogLine, got {typeof(T).Name}");
            LastOptions = options ?? LogSubscriptionOptions.Default;
            _handler = (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler;
            return new FakeSub(() => _handler = null);
        }

        public ValueTask Deliver(LogEnvelope<LocalPlayerLogLine> envelope)
        {
            var h = _handler ?? throw new InvalidOperationException("No active subscription");
            return h(envelope);
        }

        private sealed class FakeSub : ILogSubscription
        {
            private readonly Action _onDispose;
            public FakeSub(Action onDispose) { _onDispose = onDispose; }
            public string Id => "fake#discovery";
            public LogSubscriptionDiagnostics Diagnostics => new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }
            public void Dispose() => _onDispose();
        }
    }
}
