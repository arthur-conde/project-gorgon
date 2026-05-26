using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Mithril.Shared.Logging;

namespace Legolas.Tests.TestSupport;

/// <summary>
/// Test-only <see cref="ILogStreamDriver"/> implementation for the
/// archetype-B Legolas/PlayerLog migration tests (#550 PR 3). Reproduces
/// the production driver's two-phase delivery shape (yield in-memory
/// backlog as replay envelopes, then yield from a live channel) plus the
/// <see cref="LogSubscriptionOptions"/> filters this consumer relies on
/// (<see cref="ReplayMode.LiveOnly"/>, <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/>)
/// without pulling in the full <c>LogStreamDriver</c> + L0.5 router stack.
///
/// <para>Only the <see cref="LocalPlayerLogLine"/> pipe is wired — that's
/// what <c>PlayerLogIngestionService</c> consumes; other pipes throw on
/// subscribe so a misrouted test fails loud rather than hangs silently.</para>
///
/// <para>Kept local to Legolas.Tests because per-consumer needs
/// (idempotence policy per #549, fault SM thresholds) diverge from a
/// shared abstraction.
/// One file per consumer test suite is cheaper than one shared abstraction
/// per knob.</para>
/// </summary>
internal sealed class TestLogStreamDriver : ILogStreamDriver, IDisposable
{
    private readonly Pipe _localPlayer = new();
    private readonly List<IDisposable> _subscriptions = new();

    public void PushReplay(LocalPlayerLogLine line) => _localPlayer.AddReplay(line);
    public void PushLive(LocalPlayerLogLine line) => _localPlayer.PushLive(line);

    public Task DrainLocalPlayerAsync(TimeSpan? timeout = null) =>
        _localPlayer.DrainAsync(timeout ?? TimeSpan.FromSeconds(5));

    public ILogSubscription Subscribe<T>(
        Func<LogEnvelope<T>, ValueTask> handler,
        LogSubscriptionOptions? options = null) where T : class
    {
        if (typeof(T) != typeof(LocalPlayerLogLine))
            throw new ArgumentException(
                $"This test driver only supports LocalPlayerLogLine subscriptions; got {typeof(T).Name}",
                nameof(T));

        var opts = options ?? LogSubscriptionOptions.Default;
        var sub = new Subscription(
            _localPlayer,
            (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler,
            opts);
        sub.Start();
        lock (_subscriptions) _subscriptions.Add(sub);
        return sub;
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
    /// Per-payload buffer + live channel. <see cref="AddReplay"/> appends
    /// to the replay list (must be called BEFORE any Subscribe consumes
    /// the pipe); <see cref="PushLive"/> writes to the live channel.
    /// </summary>
    private sealed class Pipe
    {
        private readonly List<LocalPlayerLogLine> _replay = new();
        private readonly Channel<LocalPlayerLogLine> _live = Channel.CreateUnbounded<LocalPlayerLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcsResolved();
        private readonly object _gate = new();
        private bool _replaySnapshotted;

        internal void AddReplay(LocalPlayerLogLine line)
        {
            lock (_gate)
            {
                if (_replaySnapshotted)
                    throw new InvalidOperationException(
                        "AddReplay must be called before any Subscribe<T> consumes the pipe.");
                Interlocked.Increment(ref _pending);
                _replay.Add(line);
                Interlocked.Exchange(ref _drained, NewDrainTcs());
            }
        }

        internal void PushLive(LocalPlayerLogLine line)
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

        internal async IAsyncEnumerable<LogEnvelope<LocalPlayerLogLine>> SubscribeAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            LocalPlayerLogLine[] replaySnap;
            lock (_gate) { replaySnap = _replay.ToArray(); _replaySnapshotted = true; }
            foreach (var line in replaySnap)
            {
                if (ct.IsCancellationRequested) yield break;
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: true);
            }
            await foreach (var line in _live.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: false);
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static TaskCompletionSource NewDrainTcsResolved()
        {
            var tcs = NewDrainTcs();
            tcs.TrySetResult();
            return tcs;
        }
    }

    /// <summary>
    /// Per-subscription pump. Honors <see cref="ReplayMode.LiveOnly"/>,
    /// <see cref="ReplayMode.SinceSubscribe"/>, and
    /// <see cref="LogSubscriptionOptions.SkipProcessedHighWater"/> — the
    /// three filters this consumer's subscription composes.
    /// </summary>
    private sealed class Subscription : ILogSubscription, IDisposable
    {
        private readonly Pipe _pipe;
        private readonly Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> _handler;
        private readonly LogSubscriptionOptions _options;
        private readonly CancellationTokenSource _cts = new();
        private Task? _pumpTask;
        private int _disposed;

        public Subscription(
            Pipe pipe,
            Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> handler,
            LogSubscriptionOptions options)
        {
            _pipe = pipe;
            _handler = handler;
            _options = options;
        }

        public string Id { get; } = $"test#{Guid.NewGuid():N}";
        public LogSubscriptionDiagnostics Diagnostics =>
            new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
        public event EventHandler? StateChanged { add { } remove { } }

        public void Start() { _pumpTask = Task.Run(PumpAsync); }

        private async Task PumpAsync()
        {
            var ct = _cts.Token;
            try
            {
                await foreach (var env in _pipe.SubscribeAsync(ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;

                    // High-water filter mirrors LogStreamDriver:285-297.
                    if (_options.SkipProcessedHighWater is long hw
                        && env.Payload.Sequence <= hw)
                    {
                        _pipe.RecordDelivered();
                        continue;
                    }

                    // LiveOnly / SinceSubscribe — drop replay-phase envelopes.
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
