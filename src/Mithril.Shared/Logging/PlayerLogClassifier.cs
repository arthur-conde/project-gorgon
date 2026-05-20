using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#556) front-half component: subscribes to L0's
/// <see cref="IPlayerLogStream"/> Player.log feed, drives each line through
/// the line-local <see cref="PlayerLogLineClassifier"/>, eats the
/// <c>Actor:</c> envelope, and publishes the surviving ~5% on the single
/// unified <see cref="IClassifiedPlayerLogStream"/> pipe as
/// <see cref="IClassifiedPlayerLogLine"/> envelopes.
///
/// <para>This is the canonical ordered stream — every classified line, in
/// source-<see cref="RawLogLine.Sequence"/> order. Cross-pipe-ordering-
/// sensitive consumers subscribe here via the L1 driver. The per-Kind
/// typed pipes (<see cref="ILocalPlayerLogStream"/> / friends) are derived
/// views published by <see cref="PlayerLogPipeSplitter"/>.</para>
///
/// <para>~95% of the corpus (engine/asset noise, exception headers,
/// indented stack frames, native-address frames, the <c>entity_*</c>
/// non-actor shapes) is cheap-discarded. Genuinely unknown shapes are
/// counted and rate-limit-sampled via <see cref="IDiagnosticsSink"/> as G7
/// telemetry — they never assert-fail in production.</para>
///
/// <para><b>Lifecycle.</b> Mirrors <see cref="PlayerLogStream"/>:
/// lazy-start on first subscribe; session-replay buffer for late joiners;
/// bounded per-subscriber channel for live delivery. The replay buffer
/// holds <em>classified</em> records — a subscriber that attaches
/// mid-session sees everything since L0's session start that survived
/// classification, not the raw pre-classification feed.</para>
///
/// <para><b><see cref="LocalPlayerLogLine.Raw"/></b> (and friends) is
/// filled iff <c>captureRawAccessor</c> samples <c>true</c> at emit time —
/// sampled per-line cheaply (no per-line settings store lookup). Flip the
/// underlying setting and subsequent emissions reflect the new state;
/// existing buffered records are unaffected.</para>
///
/// <para><b>Restart-race fix (#547).</b> A new <c>RunAsync</c> is queued
/// via <see cref="Task.ContinueWith(System.Func{Task, Task})"/> so it can
/// only start after the prior task has fully unwound its
/// <c>await foreach</c> over the upstream feed — closing the window where
/// a fast unsub→sub previously spawned a second <c>RunAsync</c>
/// concurrently with the still-draining first.</para>
/// </summary>
public sealed class PlayerLogClassifier : IClassifiedPlayerLogStream, IDisposable
{
    private readonly IPlayerLogStream _upstream;
    private readonly IDiagnosticsSink? _diag;
    private readonly Func<bool> _captureRaw;
    private readonly ThrottledWarn _anomalyWarn;

    private readonly object _gate = new();
    private readonly PipeRegistry<IClassifiedPlayerLogLine> _unified = new();

    private CancellationTokenSource? _runCts;
    // Chain of consecutive RunAsync invocations. A new run is queued via
    // ContinueWith so it can only start after the prior task has fully
    // unwound its `await foreach` over the upstream feed — closing the
    // restart race (#547) where a fast unsub→sub previously spawned a
    // second RunAsync concurrently with the still-draining first.
    private Task _runChain = Task.CompletedTask;
    private long _discardCount;
    private long _anomalyCount;
    private long _samplesEmitted;
    private const int AnomalySampleLogBudget = 8;
    private const string DiagCategory = "PlayerLog.Classifier";

    public PlayerLogClassifier(
        IPlayerLogStream upstream,
        IDiagnosticsSink? diag = null,
        Func<bool>? captureRawAccessor = null)
    {
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _diag = diag;
        _captureRaw = captureRawAccessor ?? (static () => false);
        _anomalyWarn = new ThrottledWarn(diag, DiagCategory);
    }

    public IAsyncEnumerable<LogEnvelope<IClassifiedPlayerLogLine>> SubscribeWithReplayMarkerAsync(
        CancellationToken ct) => SubscribeWithMarker(_unified, ct);

    /// <summary>
    /// Diagnostics-only counters: total cheap-discards, total anomalies,
    /// total anomaly samples actually emitted to <see cref="IDiagnosticsSink"/>
    /// (rate-limited).
    /// </summary>
    public ClassifierCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new ClassifierCounters(_discardCount, _anomalyCount, _samplesEmitted);
            }
        }
    }

    public readonly record struct ClassifierCounters(long Discarded, long Anomaly, long AnomalySamplesEmitted);

    /// <summary>
    /// L1-facing subscription. Same backlog-then-live-tail shape as the
    /// L0.5 typed pipes; each yielded item is paired with the
    /// <see cref="LogEnvelope{T}.IsReplay"/> bit answered authoritatively
    /// by the structural boundary (replay yields from the snapshot; live
    /// yields from the channel branch).
    /// </summary>
    private async IAsyncEnumerable<LogEnvelope<T>> SubscribeWithMarker<T>(
        PipeRegistry<T> pipe, [EnumeratorCancellation] CancellationToken ct)
        where T : class
    {
        var channel = Channel.CreateBounded<T>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        T[] replay;
        lock (_gate)
        {
            replay = pipe.SnapshotReplay();
            pipe.AddSubscriber(channel);
            EnsureRunning();
        }

        try
        {
            // Drain replay buffer directly via yield (same bypass-the-bounded-
            // channel pattern as PlayerLogStream) so a late joiner can't lose
            // history to DropOldest when the buffer exceeds 1024 lines.
            foreach (var item in replay)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<T>(item, IsReplay: true);
            }

            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return new LogEnvelope<T>(item, IsReplay: false);
            }
        }
        finally
        {
            lock (_gate)
            {
                pipe.RemoveSubscriber(channel);
                channel.Writer.TryComplete();
                if (_unified.IsEmpty) StopRunning();
            }
        }
    }

    private void EnsureRunning()
    {
        // A healthy uncancelled run already in progress? Keep it.
        if (_runCts is { IsCancellationRequested: false } && !_runChain.IsCompleted) return;

        // Otherwise queue a new RunAsync at the tail of the chain. Using
        // ContinueWith guarantees the prior task has fully drained its
        // `await foreach` over upstream before the new one subscribes —
        // upstream sees at most one active subscription at any moment.
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _runChain = _runChain.ContinueWith(
            _ => RunChainLinkAsync(cts),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    private async Task RunChainLinkAsync(CancellationTokenSource cts)
    {
        try
        {
            // A StopRunning landing between queueing and this point makes
            // the queued link a no-op — don't subscribe to upstream just to
            // see "already canceled" on the first iteration.
            bool shouldRun;
            lock (_gate) shouldRun = !_unified.IsEmpty && !cts.IsCancellationRequested;
            if (!shouldRun) return;
            await RunAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void StopRunning()
    {
        // Signal cancellation; the chain links observe it and unwind. The
        // CTS itself is disposed by the link's finally — we DON'T null it
        // out here, because EnsureRunning needs to read IsCancellationRequested
        // to decide whether to queue a successor.
        try { _runCts?.Cancel(); } catch { }
        _unified.ClearReplay();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _diag?.Info(DiagCategory, "Subscribing to L0 Player.log feed for classification");
        try
        {
            await foreach (var raw in _upstream.SubscribeAsync(ct).ConfigureAwait(false))
            {
                try { Classify(raw); }
                catch (Exception ex)
                {
                    // Per-line containment — same #512 idiom every other consumer
                    // adopted. A throw from Classify should not kill the L0.5 loop.
                    _anomalyWarn.Warn($"Classify threw: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown / StopRunning
        }
    }

    private void Classify(RawLogLine raw)
    {
        var line = raw.Line;
        var result = PlayerLogLineClassifier.Classify(line);
        var capture = _captureRaw();
        var rawField = capture ? line : null;

        switch (result.Kind)
        {
            case PlayerLogLineClassifier.LineKind.LocalPlayer:
            {
                var data = line.Substring(result.DataStart);
                var item = new LocalPlayerLogLine(
                    raw.Timestamp, data, raw.Sequence, raw.ReadMonotonicTicks, rawField);
                Publish(item);
                break;
            }
            case PlayerLogLineClassifier.LineKind.CombatActor:
            {
                var data = line.Substring(result.DataStart);
                var item = new CombatActorLogLine(
                    raw.Timestamp, result.CombatEntityId, data, raw.Sequence, raw.ReadMonotonicTicks, rawField);
                Publish(item);
                break;
            }
            case PlayerLogLineClassifier.LineKind.SystemSignal:
            {
                var data = line.Substring(result.DataStart);
                var item = new SystemSignalLogLine(
                    raw.Timestamp, result.SystemKind, data, raw.Sequence, raw.ReadMonotonicTicks, rawField);
                Publish(item);
                break;
            }
            case PlayerLogLineClassifier.LineKind.Discard:
                lock (_gate) { _discardCount++; }
                break;
            case PlayerLogLineClassifier.LineKind.Anomaly:
                long sample;
                lock (_gate)
                {
                    _anomalyCount++;
                    sample = _samplesEmitted;
                }
                // Cap samples to a small fixed budget — a runaway anomaly
                // pattern shouldn't dominate the diagnostic log even after
                // ThrottledWarn rate-limiting; the budget plus throttle
                // together give G7's "count + sample, never assert" shape.
                if (sample < AnomalySampleLogBudget)
                {
                    var truncated = line.Length > 240 ? line.Substring(0, 240) + "…" : line;
                    _anomalyWarn.Warn($"unclassified shape: \"{truncated}\"");
                    lock (_gate) { _samplesEmitted++; }
                }
                break;
        }
    }

    private void Publish(IClassifiedPlayerLogLine item)
    {
        Channel<IClassifiedPlayerLogLine>[] snapshot;
        lock (_gate) snapshot = _unified.Append(item);
        foreach (var ch in snapshot) ch.Writer.TryWrite(item);
    }

    public void Dispose()
    {
        lock (_gate) { StopRunning(); }
    }

    /// <summary>
    /// Per-pipe subscription + session-replay buffer. Mirrors the inner
    /// structure of <see cref="PlayerLogStream"/> but generic over the
    /// emitted record type. All access is gated by the outer classifier's
    /// <c>_gate</c>; the registry does not lock internally.
    /// </summary>
    private sealed class PipeRegistry<T> where T : class
    {
        private readonly List<Channel<T>> _subs = new();
        private List<T>? _replay;

        public bool IsEmpty => _subs.Count == 0;

        public T[] SnapshotReplay() => _replay is { Count: > 0 } r ? r.ToArray() : Array.Empty<T>();

        public void AddSubscriber(Channel<T> ch)
        {
            _subs.Add(ch);
            _replay ??= new List<T>();
        }

        public void RemoveSubscriber(Channel<T> ch) => _subs.Remove(ch);

        public Channel<T>[] Append(T item)
        {
            _replay?.Add(item);
            return _subs.ToArray();
        }

        public void ClearReplay() => _replay = null;
    }
}
