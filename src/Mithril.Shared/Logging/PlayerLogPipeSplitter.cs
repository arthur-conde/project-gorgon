using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Logging;

/// <summary>
/// L0.5 (#556) back-half component: a single upstream subscription to
/// <see cref="IClassifiedPlayerLogStream"/> shared across all typed-pipe
/// subscribers, switch-on-runtime-type dispatch to the matching per-Kind
/// <see cref="PipeRegistry{T}"/>, and three public typed-pipe surfaces
/// (<see cref="ILocalPlayerLogStream"/>, <see cref="ICombatActorLogStream"/>,
/// <see cref="ISystemSignalLogStream"/>).
///
/// <para>The splitter does NOT depend on the L1
/// <see cref="LogStreamDriver"/>. It subscribes directly to
/// <see cref="IClassifiedPlayerLogStream"/>. L1's driver depends on the
/// typed-pipe interfaces that the splitter publishes; routing the
/// splitter's own consumption back through L1 would introduce a cycle, so
/// the L1 containment idiom (per-line try/catch + <see cref="ThrottledWarn"/>)
/// is applied in-house here.</para>
///
/// <para><b>Lifecycle.</b> Lazy-start on first typed-pipe subscriber;
/// stop on last typed-pipe unsubscribe. The splitter's upstream
/// subscription to the unified pipe opens and closes in step with this —
/// so the classifier's lifecycle is driven through the splitter when the
/// 5 already-migrated archetype-A consumers are the only attached
/// subscribers, and the classifier picks up additional unified-pipe
/// subscribers (Pin/Weather/Position via L1) independently.</para>
///
/// <para><b>IsReplay forwarding.</b> The
/// <see cref="LogEnvelope{T}.IsReplay"/> bit arriving on each unified
/// envelope is forwarded unchanged onto the typed-pipe envelope yielded
/// to the matching subscriber — the boundary is set by the classifier
/// and the splitter is a faithful projection.</para>
///
/// <para>Diagnostic category: <c>PlayerLog.Splitter</c>.</para>
/// </summary>
public sealed class PlayerLogPipeSplitter :
    ILocalPlayerLogStream,
    ICombatActorLogStream,
    ISystemSignalLogStream,
    IDisposable
{
    private readonly IClassifiedPlayerLogStream _upstream;
    private readonly IDiagnosticsSink? _diag;
    private readonly ThrottledWarn _faultWarn;

    private readonly object _gate = new();
    private readonly PipeRegistry<LocalPlayerLogLine> _localPlayer = new();
    private readonly PipeRegistry<CombatActorLogLine> _combat = new();
    private readonly PipeRegistry<SystemSignalLogLine> _system = new();

    private CancellationTokenSource? _runCts;
    private Task _runChain = Task.CompletedTask;
    private long _dispatchFailures;
    private const string DiagCategory = "PlayerLog.Splitter";

    public PlayerLogPipeSplitter(
        IClassifiedPlayerLogStream upstream,
        IDiagnosticsSink? diag = null)
    {
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _diag = diag;
        _faultWarn = new ThrottledWarn(diag, DiagCategory);
    }

    public IAsyncEnumerable<LocalPlayerLogLine> SubscribeAsync(CancellationToken ct) =>
        Subscribe(_localPlayer, ct);

    IAsyncEnumerable<CombatActorLogLine> ICombatActorLogStream.SubscribeAsync(CancellationToken ct) =>
        Subscribe(_combat, ct);

    IAsyncEnumerable<SystemSignalLogLine> ISystemSignalLogStream.SubscribeAsync(CancellationToken ct) =>
        Subscribe(_system, ct);

    public IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>> SubscribeWithReplayMarkerAsync(
        CancellationToken ct) => SubscribeWithMarker(_localPlayer, ct);

    IAsyncEnumerable<LogEnvelope<CombatActorLogLine>> ICombatActorLogStream.SubscribeWithReplayMarkerAsync(
        CancellationToken ct) => SubscribeWithMarker(_combat, ct);

    IAsyncEnumerable<LogEnvelope<SystemSignalLogLine>> ISystemSignalLogStream.SubscribeWithReplayMarkerAsync(
        CancellationToken ct) => SubscribeWithMarker(_system, ct);

    /// <summary>
    /// Diagnostics-only counter: total per-envelope dispatch failures
    /// observed by the splitter's upstream pump (caught + throttled-warn'd
    /// in-house since the splitter doesn't run through the L1 driver).
    /// </summary>
    public SplitterCounters Counters
    {
        get
        {
            lock (_gate)
            {
                return new SplitterCounters(_dispatchFailures);
            }
        }
    }

    public readonly record struct SplitterCounters(long DispatchFailures);

    private async IAsyncEnumerable<T> Subscribe<T>(
        PipeRegistry<T> pipe, [EnumeratorCancellation] CancellationToken ct)
        where T : class
    {
        // Unbounded with single-reader / single-writer. The previous bounded
        // 1024 + DropOldest configuration silently lost replay backlog when a
        // long-session cold-start replayed > 1024 LocalPlayer (or other-Kind)
        // lines faster than the subscriber's pump could drain — see #XXX, in
        // which the inventory producer's ~134-item ProcessAddItem block was
        // evicted by later ProcessCombatModeStatus ticks during a 12-minute
        // session, wedging the world merger on inventory's PendingFetch.
        // Unbounded mirrors the producer-side channels (SkillFrameProducer,
        // AreaLoadingFrameProducer, …): PG's source-stream rate is human-
        // bounded, so unbounded growth is bounded in practice by the source.
        // A subscriber that genuinely stalls is a bug to surface via L1
        // diagnostics, not data to silently drop.
        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
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

    private async IAsyncEnumerable<LogEnvelope<T>> SubscribeWithMarker<T>(
        PipeRegistry<T> pipe, [EnumeratorCancellation] CancellationToken ct)
        where T : class
    {
        // Unbounded — see the rationale on <see cref="Subscribe{T}"/>. The
        // marker variant carries identical drop risk under bounded sizing
        // because the L1 driver's pump runs through this surface; a long-
        // session cold-start replay can dispatch many thousands of typed
        // envelopes before the L1 pump invokes its first consumer handler.
        var channel = Channel.CreateUnbounded<MarkerItem<T>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        T[] replay;
        lock (_gate)
        {
            replay = pipe.SnapshotReplay();
            pipe.AddMarkerSubscriber(channel);
            EnsureRunning();
        }

        try
        {
            foreach (var item in replay)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<T>(item, IsReplay: true);
            }

            await foreach (var marker in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return new LogEnvelope<T>(marker.Payload, marker.IsReplay);
            }
        }
        finally
        {
            lock (_gate)
            {
                pipe.RemoveMarkerSubscriber(channel);
                channel.Writer.TryComplete();
                if (NoSubscribers()) StopRunning();
            }
        }
    }

    private bool NoSubscribers() => _localPlayer.IsEmpty && _combat.IsEmpty && _system.IsEmpty;

    private void EnsureRunning()
    {
        if (_runCts is { IsCancellationRequested: false } && !_runChain.IsCompleted) return;

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
            bool shouldRun;
            lock (_gate) shouldRun = !NoSubscribers() && !cts.IsCancellationRequested;
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
        try { _runCts?.Cancel(); } catch { }
        _localPlayer.ClearReplay();
        _combat.ClearReplay();
        _system.ClearReplay();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        _diag?.Info(DiagCategory, "Subscribing to L0.5 unified classified pipe for typed-pipe dispatch");
        try
        {
            await foreach (var envelope in _upstream.SubscribeWithReplayMarkerAsync(ct).ConfigureAwait(false))
            {
                try { Dispatch(envelope); }
                catch (Exception ex)
                {
                    // Per-envelope containment. A throw from Dispatch should
                    // not kill the splitter loop; same idiom as the L1
                    // driver's per-handler containment.
                    lock (_gate) { _dispatchFailures++; }
                    _faultWarn.Warn($"Dispatch threw: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown / StopRunning
        }
        catch (Exception ex)
        {
            // The pump itself died — upstream subscription aborted. Surface
            // as a non-throttled Error; the splitter is no longer delivering.
            _diag?.Error(DiagCategory, $"Splitter pump aborted: {ex}");
        }
    }

    private void Dispatch(LogEnvelope<IClassifiedPlayerLogLine> envelope)
    {
        // Switch on the concrete runtime type. The closed set of three
        // implementers is documented on IClassifiedPlayerLogLine; an
        // unknown type lands in the default branch where the splitter
        // counts it and ThrottledWarn-emits — never assert-fails.
        switch (envelope.Payload)
        {
            case LocalPlayerLogLine l:
                PublishLocal(l, envelope.IsReplay);
                break;
            case CombatActorLogLine c:
                PublishCombat(c, envelope.IsReplay);
                break;
            case SystemSignalLogLine s:
                PublishSystem(s, envelope.IsReplay);
                break;
            default:
                lock (_gate) { _dispatchFailures++; }
                _faultWarn.Warn(
                    $"Unknown IClassifiedPlayerLogLine concrete type: {envelope.Payload.GetType().Name}");
                break;
        }
    }

    private void PublishLocal(LocalPlayerLogLine item, bool isReplay)
    {
        Channel<LocalPlayerLogLine>[] snapshot;
        Channel<MarkerItem<LocalPlayerLogLine>>[] markerSnapshot;
        lock (_gate) (snapshot, markerSnapshot) = _localPlayer.Append(item, isReplay);
        foreach (var ch in snapshot) ch.Writer.TryWrite(item);
        foreach (var ch in markerSnapshot) ch.Writer.TryWrite(new MarkerItem<LocalPlayerLogLine>(item, isReplay));
    }

    private void PublishCombat(CombatActorLogLine item, bool isReplay)
    {
        Channel<CombatActorLogLine>[] snapshot;
        Channel<MarkerItem<CombatActorLogLine>>[] markerSnapshot;
        lock (_gate) (snapshot, markerSnapshot) = _combat.Append(item, isReplay);
        foreach (var ch in snapshot) ch.Writer.TryWrite(item);
        foreach (var ch in markerSnapshot) ch.Writer.TryWrite(new MarkerItem<CombatActorLogLine>(item, isReplay));
    }

    private void PublishSystem(SystemSignalLogLine item, bool isReplay)
    {
        Channel<SystemSignalLogLine>[] snapshot;
        Channel<MarkerItem<SystemSignalLogLine>>[] markerSnapshot;
        lock (_gate) (snapshot, markerSnapshot) = _system.Append(item, isReplay);
        foreach (var ch in snapshot) ch.Writer.TryWrite(item);
        foreach (var ch in markerSnapshot) ch.Writer.TryWrite(new MarkerItem<SystemSignalLogLine>(item, isReplay));
    }

    public void Dispose()
    {
        lock (_gate) { StopRunning(); }
    }

    /// <summary>
    /// Live items pushed to <see cref="SubscribeWithMarker{T}"/> carry the
    /// replay-marker bit alongside the payload so the splitter doesn't
    /// re-infer IsReplay from sync-vs-async timing.
    /// </summary>
    private readonly record struct MarkerItem<T>(T Payload, bool IsReplay) where T : class;

    /// <summary>
    /// Per-pipe subscription + session-replay buffer. Mirrors the
    /// classifier's nested registry but carries TWO subscriber lists in
    /// parallel — one for plain <see cref="SubscribeAsync"/> consumers
    /// (5 archetype-A migrated services), one for L1-facing marker
    /// consumers. The replay buffer holds payloads only; marker-variant
    /// subscribers receive snapshot items as IsReplay=true.
    /// </summary>
    private sealed class PipeRegistry<T> where T : class
    {
        private readonly List<Channel<T>> _subs = new();
        private readonly List<Channel<MarkerItem<T>>> _markerSubs = new();
        private List<T>? _replay;

        public bool IsEmpty => _subs.Count == 0 && _markerSubs.Count == 0;

        public T[] SnapshotReplay() => _replay is { Count: > 0 } r ? r.ToArray() : Array.Empty<T>();

        public void AddSubscriber(Channel<T> ch)
        {
            _subs.Add(ch);
            _replay ??= new List<T>();
        }

        public void RemoveSubscriber(Channel<T> ch) => _subs.Remove(ch);

        public void AddMarkerSubscriber(Channel<MarkerItem<T>> ch)
        {
            _markerSubs.Add(ch);
            _replay ??= new List<T>();
        }

        public void RemoveMarkerSubscriber(Channel<MarkerItem<T>> ch) => _markerSubs.Remove(ch);

        public (Channel<T>[] Plain, Channel<MarkerItem<T>>[] Marker) Append(T item, bool isReplay)
        {
            // Always append to the typed-pipe replay buffer regardless of
            // the upstream IsReplay bit — a late-joining typed-pipe
            // subscriber must observe the full session history through
            // its own snapshot drain, matching the pre-#556 router. The
            // classifier's separate unified-pipe buffer serves L1
            // unified-pipe subscribers only; typed-pipe consumers see this
            // buffer.
            _ = isReplay;
            _replay?.Add(item);
            return (_subs.ToArray(), _markerSubs.ToArray());
        }

        public void ClearReplay() => _replay = null;
    }
}
