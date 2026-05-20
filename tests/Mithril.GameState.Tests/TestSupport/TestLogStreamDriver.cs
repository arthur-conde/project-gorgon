using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Logging;

namespace Mithril.GameState.Tests.TestSupport;

/// <summary>
/// Test-only <see cref="ILogStreamDriver"/> implementation used by the
/// archetype-A GameState producer tests (post-#550 PR 2). Reproduces the
/// driver's two-phase delivery shape ("yield the in-memory backlog as
/// replay, then yield from a live channel") for each of the four typed
/// payloads (<see cref="LocalPlayerLogLine"/>, <see cref="CombatActorLogLine"/>,
/// <see cref="SystemSignalLogLine"/>, <see cref="RawLogLine"/>) without
/// pulling in the full <see cref="LogStreamDriver"/> + L0.5 router stack.
///
/// <para>Production drivers source <c>IsReplay</c> from the L0.5 router's
/// <c>SubscribeWithReplayMarkerAsync</c> boundary; here we mint it the same
/// way (replay items first, live items after a <see cref="Drain{T}"/>-style
/// gate) so the producer's handler observes the same envelope shape as in
/// production.</para>
///
/// <para><b>Drain semantics.</b> Each subscription's pump pulls every replay
/// item out of an in-memory list, then awaits the live channel. The
/// <see cref="DrainAsync{T}"/> overloads expose a deterministic "all queued
/// items have been delivered to the handler" signal — equivalent to the
/// pre-L1 <c>ScriptedStream.WaitForDrainAsync</c> primitive that every
/// archetype-A test used.</para>
/// </summary>
public sealed class TestLogStreamDriver : ILogStreamDriver, IDisposable
{
    private readonly Pipe<LocalPlayerLogLine> _localPlayer = new();
    private readonly Pipe<CombatActorLogLine> _combat = new();
    private readonly Pipe<SystemSignalLogLine> _system = new();
    private readonly Pipe<IClassifiedPlayerLogLine> _classified = new();
    private readonly Pipe<RawLogLine> _chat = new();
    private readonly List<IDisposable> _subscriptions = new();

    // PushReplay / PushLive mirror to the unified pipe so a test pushing a
    // typed envelope to the driver looks the same to a typed subscriber AND
    // to a unified (IClassifiedPlayerLogLine) subscriber — matching the
    // production splitter+classifier pair from #556. Tests pushing typed
    // envelopes don't need a second "push to unified" call.
    public void PushReplay(LocalPlayerLogLine line)
    {
        _localPlayer.AddReplay(line);
        _classified.AddReplay(line);
    }
    public void PushLive(LocalPlayerLogLine line)
    {
        _localPlayer.PushLive(line);
        _classified.PushLive(line);
    }
    public void PushReplay(SystemSignalLogLine line)
    {
        _system.AddReplay(line);
        _classified.AddReplay(line);
    }
    public void PushLive(SystemSignalLogLine line)
    {
        _system.PushLive(line);
        _classified.PushLive(line);
    }
    public void PushReplay(CombatActorLogLine line)
    {
        _combat.AddReplay(line);
        _classified.AddReplay(line);
    }
    public void PushLive(CombatActorLogLine line)
    {
        _combat.PushLive(line);
        _classified.PushLive(line);
    }
    public void PushReplay(RawLogLine line) => _chat.AddReplay(line);
    public void PushLive(RawLogLine line) => _chat.PushLive(line);

    public Task DrainLocalPlayerAsync(TimeSpan? timeout = null) =>
        _localPlayer.DrainAsync(timeout ?? TimeSpan.FromSeconds(5));
    public Task DrainSystemAsync(TimeSpan? timeout = null) =>
        _system.DrainAsync(timeout ?? TimeSpan.FromSeconds(5));
    public Task DrainCombatAsync(TimeSpan? timeout = null) =>
        _combat.DrainAsync(timeout ?? TimeSpan.FromSeconds(5));
    public Task DrainClassifiedAsync(TimeSpan? timeout = null) =>
        _classified.DrainAsync(timeout ?? TimeSpan.FromSeconds(5));
    public Task DrainChatAsync(TimeSpan? timeout = null) =>
        _chat.DrainAsync(timeout ?? TimeSpan.FromSeconds(5));

    public ILogSubscription Subscribe<T>(
        Func<LogEnvelope<T>, ValueTask> handler,
        LogSubscriptionOptions? options = null) where T : class
    {
        var opts = options ?? LogSubscriptionOptions.Default;
        if (typeof(T) == typeof(LocalPlayerLogLine))
        {
            var sub = new Subscription<LocalPlayerLogLine>(
                _localPlayer,
                (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler,
                opts);
            sub.Start();
            lock (_subscriptions) _subscriptions.Add(sub);
            return sub;
        }
        if (typeof(T) == typeof(SystemSignalLogLine))
        {
            var sub = new Subscription<SystemSignalLogLine>(
                _system,
                (Func<LogEnvelope<SystemSignalLogLine>, ValueTask>)(object)handler,
                opts);
            sub.Start();
            lock (_subscriptions) _subscriptions.Add(sub);
            return sub;
        }
        if (typeof(T) == typeof(CombatActorLogLine))
        {
            var sub = new Subscription<CombatActorLogLine>(
                _combat,
                (Func<LogEnvelope<CombatActorLogLine>, ValueTask>)(object)handler,
                opts);
            sub.Start();
            lock (_subscriptions) _subscriptions.Add(sub);
            return sub;
        }
        if (typeof(T) == typeof(IClassifiedPlayerLogLine))
        {
            // Unified pipe (#556). Same exact-equality dispatch as
            // production LogStreamDriver — a concrete-type Subscribe (e.g.
            // Subscribe<LocalPlayerLogLine>) still hits the typed-pipe
            // branch above, not this one.
            var sub = new Subscription<IClassifiedPlayerLogLine>(
                _classified,
                (Func<LogEnvelope<IClassifiedPlayerLogLine>, ValueTask>)(object)handler,
                opts);
            sub.Start();
            lock (_subscriptions) _subscriptions.Add(sub);
            return sub;
        }
        if (typeof(T) == typeof(RawLogLine))
        {
            // Chat — IsReplay always false per the production driver's
            // chat path.
            var sub = new Subscription<RawLogLine>(
                _chat,
                (Func<LogEnvelope<RawLogLine>, ValueTask>)(object)handler,
                opts);
            sub.Start();
            lock (_subscriptions) _subscriptions.Add(sub);
            return sub;
        }
        throw new ArgumentException(
            $"TestLogStreamDriver does not support subscriptions of type {typeof(T).Name}",
            nameof(T));
    }

    public void Dispose()
    {
        IDisposable[] toDispose;
        lock (_subscriptions)
        {
            toDispose = _subscriptions.ToArray();
            _subscriptions.Clear();
        }
        foreach (var s in toDispose) s.Dispose();
    }

    /// <summary>
    /// Per-payload-type buffer + live channel. One <see cref="Pipe{T}"/> per
    /// upstream pipe; the test pushes replay items via <see cref="AddReplay"/>
    /// before any subscription starts, then live items via
    /// <see cref="PushLive"/>. The TCS pending count drives
    /// <see cref="DrainAsync"/>.
    /// </summary>
    internal sealed class Pipe<T> where T : class
    {
        private readonly List<T> _replay = new();
        private readonly Channel<T> _live = Channel.CreateUnbounded<T>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcsResolved();
        private readonly object _gate = new();
        private bool _replaySnapshotted;

        /// <summary>
        /// Append a replay-phase item. MUST be called BEFORE any subscription
        /// starts consuming the pipe — once the pump's <see cref="SubscribeAsync"/>
        /// snapshots the replay list, late additions are silently dropped (the
        /// snapshot already passed, the pump never delivers, <see cref="DrainAsync"/>
        /// times out). Throws if called after the snapshot has been taken so the
        /// misuse fails fast instead of hanging.
        /// </summary>
        internal void AddReplay(T line)
        {
            lock (_gate)
            {
                if (_replaySnapshotted)
                    throw new InvalidOperationException(
                        "AddReplay must be called before any Subscribe<T> consumes the pipe — " +
                        "call it during setup, not after the pump starts.");
                Interlocked.Increment(ref _pending);
                _replay.Add(line);
                Interlocked.Exchange(ref _drained, NewDrainTcs());
            }
        }

        internal void PushLive(T line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _live.Writer.TryWrite(line);
        }

        internal void RecordDelivered()
        {
            if (Interlocked.Decrement(ref _pending) == 0)
                _drained.TrySetResult();
        }

        internal Task DrainAsync(TimeSpan timeout) =>
            _drained.Task.WaitAsync(timeout);

        private static TaskCompletionSource NewDrainTcsResolved()
        {
            var tcs = NewDrainTcs();
            tcs.TrySetResult();
            return tcs;
        }

        internal async IAsyncEnumerable<LogEnvelope<T>> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            // Snapshot the replay list so a concurrent AddReplay after the
            // pump starts doesn't get clobbered into the replay phase. Setting
            // _replaySnapshotted under the same lock makes subsequent AddReplay
            // calls fail fast rather than hang DrainAsync (see AddReplay doc).
            T[] replaySnap;
            lock (_gate) { replaySnap = _replay.ToArray(); _replaySnapshotted = true; }
            foreach (var line in replaySnap)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<T>(line, IsReplay: true);
            }
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return new LogEnvelope<T>(line, IsReplay: false);
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Subscription<T> : ILogSubscription, IDisposable where T : class
    {
        private readonly Pipe<T> _pipe;
        private readonly Func<LogEnvelope<T>, ValueTask> _handler;
        private readonly LogSubscriptionOptions _options;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pumpTask;
        private int _disposed;

        public Subscription(
            Pipe<T> pipe, Func<LogEnvelope<T>, ValueTask> handler, LogSubscriptionOptions options)
        {
            _pipe = pipe;
            _handler = handler;
            _options = options;
        }

        public string Id { get; } = $"test#{Guid.NewGuid():N}";
        public LogSubscriptionDiagnostics Diagnostics =>
            new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
        public event EventHandler? StateChanged { add { } remove { } }

        public void Start()
        {
            _pumpTask = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            var ct = _cts.Token;
            try
            {
                await foreach (var env in _pipe.SubscribeAsync(ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;

                    // Honor LiveOnly / SinceSubscribe: drop replay-phase
                    // envelopes. archetype-A uses FromSessionStart so this
                    // branch is unused for those producers, but the test
                    // helper still respects the option for completeness.
                    if ((_options.ReplayMode == ReplayMode.LiveOnly ||
                         _options.ReplayMode == ReplayMode.SinceSubscribe) &&
                        env.IsReplay)
                    {
                        _pipe.RecordDelivered();
                        continue;
                    }

                    try { await _handler(env).ConfigureAwait(false); }
                    catch { /* mirror driver containment: swallow */ }
                    finally { _pipe.RecordDelivered(); }
                }
            }
            catch (OperationCanceledException) { /* expected on dispose */ }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _cts.Cancel(); } catch { }
            try { _pumpTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _cts.Dispose(); } catch { }
        }
    }
}
