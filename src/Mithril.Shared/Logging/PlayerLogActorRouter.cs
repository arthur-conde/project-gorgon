using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#532) actor-keyed envelope router. Subscribes to L0's
/// <see cref="IPlayerLogStream"/> Player.log feed, runs the line-local
/// <see cref="PlayerLogClassifier"/>, eats the <c>Actor: </c> envelope, and
/// fans out typed records to three public pipes:
///
/// <list type="bullet">
///   <item><see cref="ILocalPlayerLogStream"/> — <c>[ts] LocalPlayer: Process*(…)</c></item>
///   <item><see cref="ICombatActorLogStream"/> — <c>[ts] entity_&lt;id&gt;: On*(…)</c>
///   (reserved; no consumer today)</item>
///   <item><see cref="ISystemSignalLogStream"/> — the small fixed
///   <see cref="SystemSignalKind"/> set</item>
/// </list>
///
/// <para>~95% of the corpus (engine/asset noise, exception headers,
/// indented stack frames, native-address frames, the <c>entity_*</c>
/// non-actor shapes) is cheap-discarded. Genuinely unknown shapes are
/// counted and rate-limit-sampled via <see cref="IDiagnosticsSink"/> as G7
/// telemetry — they never assert-fail in production.</para>
///
/// <para><b>Lifecycle.</b> Mirrors <see cref="PlayerLogStream"/>:
/// lazy-start on first subscribe across any of the three pipes; per-pipe
/// session-replay buffer for late joiners; bounded per-subscriber channel
/// for live delivery. The replay buffer holds <em>classified</em>
/// records — a subscriber that attaches mid-session sees everything
/// since L0's session start that survived classification, not the raw
/// pre-classification feed.</para>
///
/// <para><b><see cref="LocalPlayerLogLine.Raw"/></b> is filled iff
/// <see cref="_captureRaw"/> samples <c>true</c> at emit time — sampled
/// per-line cheaply (no per-line settings store lookup). Flip the
/// underlying setting and subsequent emissions reflect the new state;
/// existing buffered records are unaffected.</para>
///
/// <para>G6 carries the constraint that classification + discard run on
/// the L0/tailing side of the L1 channel. L1 doesn't exist yet (#511
/// deliverable 3); for this L0.5 PR the router subscribes to L0 via
/// <see cref="IPlayerLogStream"/> — so the ~95% discarded lines do cross
/// L0's bounded channel before being discarded here. Strict G6 placement
/// gets settled in the L1 PR when L0 / L1 actually separate.</para>
/// </summary>
public sealed class PlayerLogActorRouter :
    ILocalPlayerLogStream,
    ICombatActorLogStream,
    ISystemSignalLogStream,
    IDisposable
{
    private readonly IPlayerLogStream _upstream;
    private readonly IDiagnosticsSink? _diag;
    private readonly Func<bool> _captureRaw;
    private readonly ThrottledWarn _anomalyWarn;

    private readonly object _gate = new();
    private readonly PipeRegistry<LocalPlayerLogLine> _localPlayer = new();
    private readonly PipeRegistry<CombatActorLogLine> _combat = new();
    private readonly PipeRegistry<SystemSignalLogLine> _system = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private long _discardCount;
    private long _anomalyCount;
    private long _samplesEmitted;
    private const int AnomalySampleLogBudget = 8;

    public PlayerLogActorRouter(
        IPlayerLogStream upstream,
        IDiagnosticsSink? diag = null,
        Func<bool>? captureRawAccessor = null)
    {
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _diag = diag;
        _captureRaw = captureRawAccessor ?? (static () => false);
        _anomalyWarn = new ThrottledWarn(diag, "PlayerLogL05");
    }

    public IAsyncEnumerable<LocalPlayerLogLine> SubscribeAsync(CancellationToken ct) =>
        Subscribe(_localPlayer, ct);

    IAsyncEnumerable<CombatActorLogLine> ICombatActorLogStream.SubscribeAsync(CancellationToken ct) =>
        Subscribe(_combat, ct);

    IAsyncEnumerable<SystemSignalLogLine> ISystemSignalLogStream.SubscribeAsync(CancellationToken ct) =>
        Subscribe(_system, ct);

    /// <summary>
    /// Diagnostics-only counters: total cheap-discards, total anomalies,
    /// total anomaly samples actually emitted to <see cref="IDiagnosticsSink"/>
    /// (rate-limited).
    /// </summary>
    public RouterCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new RouterCounters(_discardCount, _anomalyCount, _samplesEmitted);
            }
        }
    }

    public readonly record struct RouterCounters(long Discarded, long Anomaly, long AnomalySamplesEmitted);

    private async IAsyncEnumerable<T> Subscribe<T>(PipeRegistry<T> pipe, [EnumeratorCancellation] CancellationToken ct)
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
                yield return item;
            }

            await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_gate)
            {
                pipe.RemoveSubscriber(channel);
                channel.Writer.TryComplete();
                if (NoSubscribers()) StopRunning();
            }
        }
    }

    private bool NoSubscribers() => _localPlayer.IsEmpty && _combat.IsEmpty && _system.IsEmpty;

    private void EnsureRunning()
    {
        if (_runTask is { IsCompleted: false }) return;
        _runCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_runCts.Token));
    }

    private void StopRunning()
    {
        try { _runCts?.Cancel(); } catch { }
        _runCts?.Dispose();
        _runCts = null;
        _runTask = null;
        _localPlayer.ClearReplay();
        _combat.ClearReplay();
        _system.ClearReplay();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _diag?.Info("PlayerLogL05", "Subscribing to L0 Player.log feed for classification");
        try
        {
            await foreach (var raw in _upstream.SubscribeAsync(ct).ConfigureAwait(false))
            {
                try { Route(raw); }
                catch (Exception ex)
                {
                    // Per-line containment — same #512 idiom every other consumer
                    // adopted. A throw from Route should not kill the L0.5 loop.
                    _anomalyWarn.Warn($"Route threw: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown / StopRunning
        }
    }

    private void Route(RawLogLine raw)
    {
        var line = raw.Line;
        var result = PlayerLogClassifier.Classify(line);
        var capture = _captureRaw();
        var rawField = capture ? line : null;

        switch (result.Kind)
        {
            case PlayerLogClassifier.LineKind.LocalPlayer:
            {
                var data = line.Substring(result.DataStart);
                var item = new LocalPlayerLogLine(
                    raw.Timestamp, data, raw.Sequence, raw.ReadMonotonicTicks, rawField);
                PublishLocal(item);
                break;
            }
            case PlayerLogClassifier.LineKind.CombatActor:
            {
                var data = line.Substring(result.DataStart);
                var item = new CombatActorLogLine(
                    raw.Timestamp, result.CombatEntityId, data, raw.Sequence, raw.ReadMonotonicTicks, rawField);
                PublishCombat(item);
                break;
            }
            case PlayerLogClassifier.LineKind.SystemSignal:
            {
                var data = line.Substring(result.DataStart);
                var item = new SystemSignalLogLine(
                    raw.Timestamp, result.SystemKind, data, raw.Sequence, raw.ReadMonotonicTicks, rawField);
                PublishSystem(item);
                break;
            }
            case PlayerLogClassifier.LineKind.Discard:
                lock (_gate) { _discardCount++; }
                break;
            case PlayerLogClassifier.LineKind.Anomaly:
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

    private void PublishLocal(LocalPlayerLogLine item)
    {
        Channel<LocalPlayerLogLine>[] snapshot;
        lock (_gate) snapshot = _localPlayer.Append(item);
        foreach (var ch in snapshot) ch.Writer.TryWrite(item);
    }

    private void PublishCombat(CombatActorLogLine item)
    {
        Channel<CombatActorLogLine>[] snapshot;
        lock (_gate) snapshot = _combat.Append(item);
        foreach (var ch in snapshot) ch.Writer.TryWrite(item);
    }

    private void PublishSystem(SystemSignalLogLine item)
    {
        Channel<SystemSignalLogLine>[] snapshot;
        lock (_gate) snapshot = _system.Append(item);
        foreach (var ch in snapshot) ch.Writer.TryWrite(item);
    }

    public void Dispose()
    {
        lock (_gate) { StopRunning(); }
    }

    /// <summary>
    /// Per-pipe subscription + session-replay buffer. Mirrors the inner
    /// structure of <see cref="PlayerLogStream"/> but generic over the
    /// emitted record type. All access is gated by the outer router's
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
