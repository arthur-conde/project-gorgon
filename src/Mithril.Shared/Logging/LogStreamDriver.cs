using System.Threading.Channels;
using System.Windows.Threading;
using Mithril.Shared.Diagnostics;

namespace Mithril.Shared.Logging;

/// <summary>
/// Default <see cref="ILogStreamDriver"/> implementation (#511 deliverable 3
/// / #550 PR 1). Wraps the four upstream surfaces — the three L0.5 pipes
/// (<see cref="ILocalPlayerLogStream"/>, <see cref="ICombatActorLogStream"/>,
/// <see cref="ISystemSignalLogStream"/>) plus the chat
/// <see cref="IChatLogStream"/> — and produces typed
/// <see cref="LogEnvelope{T}"/> subscriptions with consumer-side
/// containment, drop accounting, dispatcher marshalling, idempotence
/// filtering, and a fault state machine.
///
/// <para>The driver does NOT own the upstream surfaces; it consumes them
/// via the same <c>SubscribeAsync(ct)</c> pattern every consumer used
/// pre-L1. Per-subscription state lives on the
/// <see cref="LogSubscription{T}"/> handle returned to the caller; a single
/// driver instance hosts an unbounded number of concurrent subscriptions.</para>
///
/// <para>Player.log <see cref="RawLogLine"/> consumption is intentionally
/// NOT supported by the driver: every Player.log consumer should subscribe
/// through one of the three typed L0.5 pipes (capability A of #532).
/// Chat <see cref="RawLogLine"/> is the only raw surface — chat lines have
/// no classified equivalent (chat keeps regex-based recognition per the
/// #550 "what L1 explicitly does NOT own" section).</para>
///
/// <para><b><see cref="LogEnvelope{T}.IsReplay"/> determination.</b>
/// Sourced directly from each upstream pipe's L1-facing
/// <c>SubscribeWithReplayMarkerAsync</c> method (added to
/// <see cref="ILocalPlayerLogStream"/> / <see cref="ICombatActorLogStream"/>
/// / <see cref="ISystemSignalLogStream"/> in #550 PR 1, implemented on
/// <see cref="PlayerLogActorRouter"/>'s direct-yield-replay /
/// bounded-channel-live boundary). The L0.5 router can answer the
/// IsReplay question authoritatively because it owns the structural
/// boundary; L1 forwards the bit unchanged on each envelope. Chat-backed
/// subscriptions hard-code IsReplay = false (chat has no backlog by
/// construction — Divergence 1 in #549).</para>
/// </summary>
public sealed class LogStreamDriver : ILogStreamDriver, IDisposable
{
    private readonly ILocalPlayerLogStream _localPlayer;
    private readonly ICombatActorLogStream _combat;
    private readonly ISystemSignalLogStream _system;
    private readonly IChatLogStream _chat;
    private readonly LogStreamAttentionSource _attention;
    private readonly IDiagnosticsSink? _diag;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly HashSet<ISubscriptionInternal> _subs = new();
    private long _nextSubId;
    private bool _disposed;

    public LogStreamDriver(
        ILocalPlayerLogStream localPlayer,
        ICombatActorLogStream combat,
        ISystemSignalLogStream system,
        IChatLogStream chat,
        LogStreamAttentionSource attention,
        IDiagnosticsSink? diag = null,
        TimeProvider? time = null)
    {
        _localPlayer = localPlayer ?? throw new ArgumentNullException(nameof(localPlayer));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _attention = attention ?? throw new ArgumentNullException(nameof(attention));
        _diag = diag;
        _time = time ?? TimeProvider.System;
    }

    public ILogSubscription Subscribe<T>(
        Func<LogEnvelope<T>, ValueTask> handler,
        LogSubscriptionOptions? options = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_disposed) throw new ObjectDisposedException(nameof(LogStreamDriver));

        var opts = options ?? LogSubscriptionOptions.Default;
        var id = $"L1#{Interlocked.Increment(ref _nextSubId)}";

        Func<CancellationToken, IAsyncEnumerable<LogEnvelope<T>>> upstream;
        Func<T, long> sequenceOf;
        string streamKind;

        if (typeof(T) == typeof(LocalPlayerLogLine))
        {
            upstream = ct => (IAsyncEnumerable<LogEnvelope<T>>)(object)
                _localPlayer.SubscribeWithReplayMarkerAsync(ct);
            sequenceOf = item => ((LocalPlayerLogLine)(object)item).Sequence;
            streamKind = "LocalPlayer";
        }
        else if (typeof(T) == typeof(CombatActorLogLine))
        {
            upstream = ct => (IAsyncEnumerable<LogEnvelope<T>>)(object)
                _combat.SubscribeWithReplayMarkerAsync(ct);
            sequenceOf = item => ((CombatActorLogLine)(object)item).Sequence;
            streamKind = "Combat";
        }
        else if (typeof(T) == typeof(SystemSignalLogLine))
        {
            upstream = ct => (IAsyncEnumerable<LogEnvelope<T>>)(object)
                _system.SubscribeWithReplayMarkerAsync(ct);
            sequenceOf = item => ((SystemSignalLogLine)(object)item).Sequence;
            streamKind = "System";
        }
        else if (typeof(T) == typeof(RawLogLine))
        {
            // Chat — see Divergence 1 in #549. Chat has no backlog by
            // construction (the per-day file directory is seeded to
            // current-end before the first emission), so every chat
            // envelope is live. We coerce any non-LiveOnly ReplayMode to
            // LiveOnly and emit a one-time Info diagnostic so the
            // spurious knob doesn't go unnoticed in code review.
            if (opts.ReplayMode != ReplayMode.LiveOnly)
            {
                _diag?.Info(
                    "Mithril.Logging.L1",
                    $"[{id}] Chat subscription requested ReplayMode={opts.ReplayMode}; coercing to LiveOnly (chat has no backlog by construction). #549 Divergence 1.");
                opts = opts with { ReplayMode = ReplayMode.LiveOnly };
            }
            // Adapt the chat stream's single-typed yield onto the same
            // LogEnvelope<T> shape used by the L0.5 marker variants.
            // IsReplay is hard-coded to false for chat.
            upstream = ct => (IAsyncEnumerable<LogEnvelope<T>>)(object)ChatMarkerAdapter(_chat, ct);
            sequenceOf = item => ((RawLogLine)(object)item).Sequence;
            streamKind = "Chat";
        }
        else
        {
            throw new ArgumentException(
                $"L1 driver does not support subscriptions of type {typeof(T).Name}. " +
                "Supported: LocalPlayerLogLine, CombatActorLogLine, SystemSignalLogLine, RawLogLine (chat). " +
                "For Player.log RawLogLine subscribe through one of the three L0.5 pipes instead.",
                nameof(T));
        }

        var sub = new LogSubscription<T>(
            id: id,
            options: opts,
            handler: handler,
            upstream: upstream,
            sequenceOf: sequenceOf,
            streamKind: streamKind,
            attention: _attention,
            diag: _diag,
            time: _time,
            onDisposed: s => { lock (_gate) _subs.Remove(s); });

        lock (_gate) _subs.Add(sub);
        sub.Start();
        return sub;
    }

    public void Dispose()
    {
        ISubscriptionInternal[] toDispose;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _subs.ToArray();
            _subs.Clear();
        }
        foreach (var s in toDispose) s.Dispose();
    }

    /// <summary>
    /// Adapter that lifts the chat stream's single-typed
    /// <see cref="IAsyncEnumerable{RawLogLine}"/> onto the same
    /// <see cref="LogEnvelope{T}"/> shape that the L0.5 pipes yield.
    /// IsReplay is always false: chat has no backlog by construction.
    /// </summary>
    private static async System.Collections.Generic.IAsyncEnumerable<LogEnvelope<RawLogLine>>
        ChatMarkerAdapter(
            IChatLogStream chat,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var line in chat.SubscribeAsync(ct).ConfigureAwait(false))
        {
            yield return new LogEnvelope<RawLogLine>(line, IsReplay: false);
        }
    }

    /// <summary>
    /// Non-generic handle the driver uses to track subscriptions in its
    /// disposal set without committing to a particular <typeparamref name="T"/>.
    /// </summary>
    internal interface ISubscriptionInternal : ILogSubscription { }

    /// <summary>
    /// Concrete subscription. One per <see cref="Subscribe{T}"/> call. Owns
    /// a single upstream subscription, the pump task, and the per-
    /// subscription state (counters + fault SM).
    /// </summary>
    private sealed class LogSubscription<T> : ISubscriptionInternal where T : class
    {
        private readonly string _id;
        private readonly LogSubscriptionOptions _options;
        private readonly Func<LogEnvelope<T>, ValueTask> _handler;
        private readonly Func<CancellationToken, IAsyncEnumerable<LogEnvelope<T>>> _upstream;
        private readonly Func<T, long> _sequenceOf;
        private readonly string _streamKind;
        private readonly LogStreamAttentionSource _attention;
        private readonly IDiagnosticsSink? _diag;
        private readonly Action<ISubscriptionInternal> _onDisposed;
        private readonly ThrottledWarn _warn;
        private readonly string _diagCategory;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pumpTask;
        private IDeliveryBridge? _bridge;

        // Counters — Interlocked for thread-safe lock-free reads/writes from
        // both the pump and the consumer's Diagnostics snapshot.
        private long _delivered;
        private long _dropped;
        private long _handlerFailures;
        private long _highWaterSkipped;
        private int _consecutiveFailures;
        private int _state; // LogSubscriptionState as int

        private int _disposed; // 0 = alive, 1 = disposed

        public LogSubscription(
            string id,
            LogSubscriptionOptions options,
            Func<LogEnvelope<T>, ValueTask> handler,
            Func<CancellationToken, IAsyncEnumerable<LogEnvelope<T>>> upstream,
            Func<T, long> sequenceOf,
            string streamKind,
            LogStreamAttentionSource attention,
            IDiagnosticsSink? diag,
            TimeProvider time,
            Action<ISubscriptionInternal> onDisposed)
        {
            _id = id;
            _options = options;
            _handler = handler;
            _upstream = upstream;
            _sequenceOf = sequenceOf;
            _streamKind = streamKind;
            _attention = attention;
            _diag = diag;
            _onDisposed = onDisposed;
            _diagCategory = options.DiagnosticCategory ?? $"Mithril.Logging.L1.{streamKind}";
            _warn = new ThrottledWarn(diag, _diagCategory, time: time);
        }

        public string Id => _id;

        public LogSubscriptionDiagnostics Diagnostics => new(
            Delivered: Interlocked.Read(ref _delivered),
            Dropped: Interlocked.Read(ref _dropped),
            HandlerFailures: Interlocked.Read(ref _handlerFailures),
            ConsecutiveFailures: Volatile.Read(ref _consecutiveFailures),
            HighWaterSkipped: Interlocked.Read(ref _highWaterSkipped),
            State: (LogSubscriptionState)Volatile.Read(ref _state));

        public event EventHandler? StateChanged;

        internal void Start()
        {
            _bridge = CreateDeliveryBridge();
            // The pump runs on TaskScheduler.Default — never inline on the
            // caller — so Subscribe<T> returns synchronously without
            // pumping the first envelope on the caller's thread.
            _pumpTask = Task.Run(PumpAsync, CancellationToken.None);
        }

        private async Task PumpAsync()
        {
            var ct = _cts.Token;
            var bridge = _bridge!;
            // IsReplay is sourced directly from the upstream's structural
            // boundary (L0.5 SubscribeWithReplayMarkerAsync; chat is always
            // live). We never reach for a sync-vs-async timing heuristic.

            try
            {
                await foreach (var envelope in _upstream(ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;
                    var seq = _sequenceOf(envelope.Payload);

                    // High-water filter (capability F) — applies regardless
                    // of replay/live phase. Today's archetype-A consumers
                    // (idempotent state-rebuilders) decline this; the four
                    // archetype-B consumers that take it (Samwise, Pippin,
                    // Saruman/Discovery, Legolas×2) get restart-safe dedup
                    // with one line of subscription config.
                    if (_options.SkipProcessedHighWater is long hw && seq <= hw)
                    {
                        Interlocked.Increment(ref _highWaterSkipped);
                        continue;
                    }

                    // LiveOnly — drop replay-phase emissions
                    if (_options.ReplayMode == ReplayMode.LiveOnly && envelope.IsReplay)
                    {
                        continue;
                    }

                    await bridge.DeliverAsync(envelope, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { /* expected on Dispose */ }
            catch (Exception ex)
            {
                // The pump itself threw — upstream subscription died. Emit a
                // non-throttled Error; the subscription is effectively over.
                _diag?.Error(_diagCategory, $"[{_id}] L1 pump aborted: {ex}");
            }
            finally
            {
                bridge.Complete();
            }
        }

        private IDeliveryBridge CreateDeliveryBridge() =>
            _options.DeliveryContext switch
            {
                DeliveryContext.MarshaledContext m => new MarshaledBridge(this, m.Dispatcher),
                _ => new InlineBridge(this),
            };

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            // Resolve any attention entry — flapping out of degraded on
            // disposal is the right shape (a disposed subscription doesn't
            // need attention).
            _attention.NotifyHealthy(_id);
            _onDisposed(this);
            // Don't block on the pump task — disposal is synchronous and
            // the pump will unwind on cancellation. The CTS isn't disposed
            // here either; doing so during pump unwind would race the
            // upstream's `await foreach`.
        }

        /// <summary>
        /// Invoked by the delivery bridge after each successful invocation
        /// of the consumer's handler. Resets the consecutive-failure count
        /// and resolves a degraded state if applicable.
        /// </summary>
        internal void RecordDelivered()
        {
            Interlocked.Increment(ref _delivered);
            var prior = Interlocked.Exchange(ref _consecutiveFailures, 0);
            if (prior > 0 && Volatile.Read(ref _state) == (int)LogSubscriptionState.Degraded)
            {
                Volatile.Write(ref _state, (int)LogSubscriptionState.Healthy);
                _attention.NotifyHealthy(_id);
                StateChanged?.Invoke(this, EventArgs.Empty);
                _diag?.Info(_diagCategory, $"[{_id}] subscription recovered (Healthy)");
            }
        }

        /// <summary>
        /// Invoked by the delivery bridge when the consumer's handler
        /// throws. Increments failure counters and transitions to
        /// <see cref="LogSubscriptionState.Degraded"/> if the threshold is
        /// crossed.
        /// </summary>
        internal void RecordHandlerFailure(Exception ex)
        {
            Interlocked.Increment(ref _handlerFailures);
            var consecutive = Interlocked.Increment(ref _consecutiveFailures);

            // Rate-limited Warn — the routine "occasional bad line" path.
            _warn.Warn($"[{_id}] handler threw: {ex.Message}");

            // Fault SM — degrade on threshold cross. We compare to the
            // EXACT threshold so the transition fires only once per
            // crossing rather than on every subsequent failure.
            if (consecutive == _options.DegradedAfterConsecutiveFailures &&
                Interlocked.CompareExchange(ref _state,
                    (int)LogSubscriptionState.Degraded,
                    (int)LogSubscriptionState.Healthy) == (int)LogSubscriptionState.Healthy)
            {
                _attention.NotifyDegraded(_id);
                StateChanged?.Invoke(this, EventArgs.Empty);
                // One non-throttled Error on entry — the visibility
                // promise of capability G ("alive but silently dead" →
                // "alive and visibly degraded").
                _diag?.Error(
                    _diagCategory,
                    $"[{_id}] subscription degraded after {consecutive} consecutive handler failures. " +
                    $"Last error: {ex.Message}. Continuing to deliver — handler will be retried on every subsequent envelope.");
            }
        }

        /// <summary>Test hook — increments the drop counter externally.</summary>
        internal void RecordDrop() => Interlocked.Increment(ref _dropped);

        // === Delivery bridges ===

        /// <summary>
        /// Boundary between the pump (upstream side) and the consumer's
        /// handler (delivery side). Owns containment, marshalling, and
        /// the bounded queue that powers drop accounting under
        /// <see cref="DeliveryContext.MarshaledContext"/>.
        /// </summary>
        private interface IDeliveryBridge
        {
            ValueTask DeliverAsync(LogEnvelope<T> envelope, CancellationToken ct);
            void Complete();
        }

        /// <summary>
        /// Inline bridge — invokes the handler on the pump thread directly.
        /// No queue, no marshalling. The handler's sync/async cost happens
        /// inside the pump's <c>await foreach</c>.
        /// </summary>
        private sealed class InlineBridge : IDeliveryBridge
        {
            private readonly LogSubscription<T> _sub;
            public InlineBridge(LogSubscription<T> sub) { _sub = sub; }

            public async ValueTask DeliverAsync(LogEnvelope<T> envelope, CancellationToken ct)
            {
                try
                {
                    await _sub._handler(envelope).ConfigureAwait(false);
                    _sub.RecordDelivered();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Disposal mid-handler — not a failure.
                }
                catch (Exception ex)
                {
                    _sub.RecordHandlerFailure(ex);
                }
            }

            public void Complete() { /* nothing to flush */ }
        }

        /// <summary>
        /// Marshaled bridge — every envelope is queued through a bounded
        /// channel, the consumer's handler runs on the supplied
        /// <see cref="Dispatcher"/>. Cross-thread mutation of bound
        /// collections is structurally impossible: the handler can only
        /// be invoked via <see cref="Dispatcher.InvokeAsync"/>, which by
        /// construction switches to the dispatcher's thread before the
        /// delegate runs.
        /// </summary>
        private sealed class MarshaledBridge : IDeliveryBridge
        {
            private const int QueueCapacity = 1024;
            private readonly LogSubscription<T> _sub;
            private readonly Dispatcher _dispatcher;
            private readonly Channel<LogEnvelope<T>> _queue;
            private readonly Task _drainTask;

            public MarshaledBridge(LogSubscription<T> sub, Dispatcher dispatcher)
            {
                _sub = sub;
                _dispatcher = dispatcher;
                _queue = Channel.CreateBounded<LogEnvelope<T>>(new BoundedChannelOptions(QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = true,
                });
                _drainTask = Task.Run(DrainAsync);
            }

            public ValueTask DeliverAsync(LogEnvelope<T> envelope, CancellationToken ct)
            {
                // Bounded channel with DropOldest. We probe Reader.Count
                // before the write so we can attribute the drop: if the
                // channel was at capacity before TryWrite, an item was
                // discarded by DropOldest semantics.
                var preCount = _queue.Reader.Count;
                _queue.Writer.TryWrite(envelope);
                if (preCount >= QueueCapacity) _sub.RecordDrop();
                return ValueTask.CompletedTask;
            }

            public void Complete()
            {
                _queue.Writer.TryComplete();
            }

            private async Task DrainAsync()
            {
                try
                {
                    await foreach (var envelope in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                    {
                        // Capture the handler's failure inside the
                        // InvokeAsync delegate so the failure shape is
                        // identical between Inline and Marshaled.
                        Exception? failure = null;
                        try
                        {
                            await _dispatcher.InvokeAsync(async () =>
                            {
                                try { await _sub._handler(envelope).ConfigureAwait(true); }
                                catch (Exception ex) { failure = ex; }
                            }).Task.ConfigureAwait(false);
                        }
                        catch (TaskCanceledException) { return; }

                        if (failure is not null) _sub.RecordHandlerFailure(failure);
                        else _sub.RecordDelivered();
                    }
                }
                catch (Exception ex)
                {
                    _sub._diag?.Error(_sub._diagCategory, $"[{_sub._id}] marshaled bridge drain aborted: {ex}");
                }
            }
        }
    }
}
