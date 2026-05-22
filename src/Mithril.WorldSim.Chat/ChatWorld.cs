using Mithril.WorldSim.Chat.Internal;

namespace Mithril.WorldSim.Chat;

/// <summary>
/// Concrete <see cref="IChatWorld"/> implementation (issue #617 — Phase 0
/// shell, sibling of <c>PlayerWorld</c> from #616). Owns the merger over
/// registered producers, the clock, the bus, and the dispatch graph. Folders
/// and composers register here but the shell itself wires no specific folders
/// — those land via #602 (chat-inventory mirror) and #603 (chat-WoP spent).
///
/// <para><b>Implementation note.</b> The merger / dispatch-graph code is
/// duplicated from <c>PlayerWorld</c> by design (principle 2 — "sealed at the
/// bus"). A future third world or a stress-tested second-world bug would
/// justify extracting a shared base in <c>Mithril.WorldSim.Core</c>;
/// pre-extraction at the second-mover boundary risks over-fitting the
/// abstraction to the two existing shapes.</para>
///
/// <para><b>Determinism</b> — frames are merged by timestamp; ties broken by
/// declared <see cref="IFrameProducer{TPayload}.Priority"/> (lower first),
/// then by registration order. Producers must emit in ascending timestamp
/// order per the producer contract; the world clamps late frames to the
/// current clock value.</para>
///
/// <para><b>Mode</b> — the world starts in <see cref="WorldMode.Replaying"/>
/// when any mode-aware producer is registered, else <see cref="WorldMode.Live"/>.
/// Each time a mode-aware producer's <see cref="IModeAwareFrameProducer{TPayload}.ReachedLive"/>
/// task completes, the world re-evaluates: if all are live, it flips and
/// emits a <see cref="ModeChanged"/> frame on the bus before dispatching the
/// next frame.</para>
///
/// <para><b>Dispatch graph</b> — per applied frame: route by payload type to
/// its folder (one folder per type) → collect change events → enqueue each
/// event onto a work-queue → for each event, dispatch to composers whose
/// <see cref="IComposer.Subscribes"/> contains the event's runtime type →
/// publish each composer-emitted <see cref="IFrame"/> to the bus AND enqueue
/// its payload for downstream composers. Resolution ends when the queue
/// drains (principle 11 — finite DAG, no merger re-entry).</para>
/// </summary>
public sealed class ChatWorld : IChatWorld, IAsyncDisposable
{
    private readonly WorldClock _clock = new();
    private readonly WorldEventBus _bus = new();

    private readonly List<RegisteredProducer> _producers = new();
    private readonly Dictionary<Type, IFolderInvoker> _folders = new();
    private readonly Dictionary<Type, List<IComposer>> _composersByType = new();
    private readonly List<IComposer> _composers = new();

    private bool _started;

    public IWorldClock Clock => _clock;
    public IWorldEventBus Bus => _bus;

    public void RegisterProducer<T>(IFrameProducer<T> producer)
    {
        ArgumentNullException.ThrowIfNull(producer);
        EnsureNotStarted();
        _producers.Add(new RegisteredProducer(
            new ProducerAdapter<T>(producer),
            producer.Priority,
            _producers.Count,
            (producer as IModeAwareFrameProducer<T>)?.ReachedLive));
    }

    public void RegisterFolder<T>(IFolder<T> folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        EnsureNotStarted();
        if (_folders.ContainsKey(typeof(T)))
        {
            throw new InvalidOperationException(
                $"A folder is already registered for payload type {typeof(T).FullName}. " +
                "One folder per payload type — see IWorld.RegisterFolder doc.");
        }
        _folders[typeof(T)] = new FolderInvoker<T>(folder);
    }

    public void RegisterComposer(IComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        EnsureNotStarted();
        _composers.Add(composer);
        foreach (var t in composer.Subscribes)
        {
            if (!_composersByType.TryGetValue(t, out var list))
            {
                list = new List<IComposer>();
                _composersByType[t] = list;
            }
            list.Add(composer);
        }
    }

    public async Task StartMerger(CancellationToken ct)
    {
        EnsureNotStarted();
        _started = true;

        if (!_producers.Any(p => p.ReachedLive is not null))
        {
            _clock.SetMode(WorldMode.Live);
        }

        await RunMergerAsync(ct).ConfigureAwait(false);
    }

    private async Task RunMergerAsync(CancellationToken ct)
    {
        if (_producers.Count == 0)
        {
            UpdateModeIfReady();
            return;
        }

        var states = _producers
            .Select(p => new ProducerRuntimeState(p, p.Adapter.GetEnumerator(ct)))
            .ToList();

        try
        {
            foreach (var s in states)
            {
                s.PendingFetch = s.Enumerator.MoveNextAsync(ct);
            }

            while (!ct.IsCancellationRequested)
            {
                foreach (var s in states)
                {
                    if (s.Exhausted || s.HasHead) continue;
                    bool hasMore;
                    try
                    {
                        hasMore = await s.PendingFetch.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        s.Exhausted = true;
                        continue;
                    }
                    if (!hasMore)
                    {
                        s.Exhausted = true;
                    }
                    else
                    {
                        s.Head = s.Enumerator.Current;
                        s.HasHead = true;
                    }
                }

                UpdateModeIfReady();

                ProducerRuntimeState? winner = null;
                foreach (var s in states)
                {
                    if (!s.HasHead) continue;
                    if (winner is null || Compare(s, winner) < 0)
                    {
                        winner = s;
                    }
                }

                if (winner is null) break;

                var frame = winner.Head!;
                winner.HasHead = false;
                winner.Head = null;
                winner.PendingFetch = winner.Enumerator.MoveNextAsync(ct);

                DispatchFrame(frame);
            }
        }
        finally
        {
            foreach (var s in states)
            {
                try
                {
                    await s.Enumerator.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Enumerator disposal failures are not load-bearing — the
                    // cancellation token is the merger's primary shutdown
                    // signal; producers must honour it.
                }
            }
        }
    }

    private static int Compare(ProducerRuntimeState a, ProducerRuntimeState b)
    {
        var byTs = a.Head!.Timestamp.CompareTo(b.Head!.Timestamp);
        if (byTs != 0) return byTs;
        var byPriority = a.Registered.Priority.CompareTo(b.Registered.Priority);
        if (byPriority != 0) return byPriority;
        return a.Registered.RegisteredIndex.CompareTo(b.Registered.RegisteredIndex);
    }

    private void UpdateModeIfReady()
    {
        if (_clock.Mode == WorldMode.Live) return;

        foreach (var p in _producers)
        {
            if (p.ReachedLive is { IsCompleted: false }) return;
        }

        var transitionAt = _clock.Now;
        _clock.SetMode(WorldMode.Live);

        if (_clock.Frame > 0)
        {
            _bus.Publish(new Frame<ModeChanged>(
                transitionAt,
                new ModeChanged(WorldMode.Replaying, WorldMode.Live, transitionAt)));
        }
    }

    private void DispatchFrame(IFrame frame)
    {
        _clock.Advance(frame.Timestamp);

        if (!_folders.TryGetValue(frame.PayloadType, out var folder))
        {
            return;
        }

        var changes = folder.Apply(frame, _clock);
        if (changes.Count == 0) return;

        var workQueue = new Queue<object>(changes.Count);
        foreach (var c in changes)
        {
            // Surface folder change events on the bus AS WELL AS routing them
            // to intra-world composers — parity with PlayerWorld. See
            // PlayerWorld.DispatchFrame for the design rationale (principle 4).
            _bus.PublishChangeEvent(c, frame.Timestamp);
            workQueue.Enqueue(c);
        }

        while (workQueue.Count > 0)
        {
            var payload = workQueue.Dequeue();
            var payloadType = payload.GetType();
            if (!_composersByType.TryGetValue(payloadType, out var composers)) continue;

            var snapshot = composers.ToArray();
            foreach (var composer in snapshot)
            {
                var emissions = composer.Observe(payload, _clock);
                if (emissions.Count == 0) continue;
                foreach (var emitted in emissions)
                {
                    _bus.Publish(emitted);
                    workQueue.Enqueue(emitted.Payload);
                }
            }
        }
    }

    private void EnsureNotStarted()
    {
        if (_started)
        {
            throw new InvalidOperationException(
                "Cannot register producers/folders/composers after StartAsync — " +
                "the registration set closes at start (IWorld.StartAsync doc).");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private readonly record struct RegisteredProducer(
        IProducerAdapter Adapter,
        int Priority,
        int RegisteredIndex,
        Task? ReachedLive);

    private sealed class ProducerRuntimeState
    {
        public RegisteredProducer Registered { get; }
        public IProducerAdapterEnumerator Enumerator { get; }
        public ValueTask<bool> PendingFetch;
        public IFrame? Head;
        public bool HasHead;
        public bool Exhausted;

        public ProducerRuntimeState(RegisteredProducer registered, IProducerAdapterEnumerator enumerator)
        {
            Registered = registered;
            Enumerator = enumerator;
        }
    }

    // ── Producer adapter ─────────────────────────────────────────────────
    // Same shape as PlayerWorld's adapter — boxes typed IAsyncEnumerable
    // behind a non-generic surface so the merger holds a heterogeneous
    // producer list without going generic over every payload type.

    private interface IProducerAdapter
    {
        IProducerAdapterEnumerator GetEnumerator(CancellationToken ct);
    }

    private interface IProducerAdapterEnumerator : IAsyncDisposable
    {
        ValueTask<bool> MoveNextAsync(CancellationToken ct);
        IFrame Current { get; }
    }

    private sealed class ProducerAdapter<T> : IProducerAdapter
    {
        private readonly IFrameProducer<T> _producer;

        public ProducerAdapter(IFrameProducer<T> producer) => _producer = producer;

        public IProducerAdapterEnumerator GetEnumerator(CancellationToken ct)
        {
            var inner = _producer.SubscribeAsync(ct).GetAsyncEnumerator(ct);
            return new Enumerator(inner);
        }

        private sealed class Enumerator : IProducerAdapterEnumerator
        {
            private readonly IAsyncEnumerator<Frame<T>> _inner;
            private Frame<T> _current;

            public Enumerator(IAsyncEnumerator<Frame<T>> inner) => _inner = inner;

            public IFrame Current => _current;

            public async ValueTask<bool> MoveNextAsync(CancellationToken ct)
            {
                _ = ct; // honoured by the inner enumerator already
                if (await _inner.MoveNextAsync().ConfigureAwait(false))
                {
                    _current = _inner.Current;
                    return true;
                }
                return false;
            }

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }

    // ── Folder invoker ───────────────────────────────────────────────────

    private interface IFolderInvoker
    {
        IReadOnlyList<IChangeEvent> Apply(IFrame frame, IWorldClock clock);
    }

    private sealed class FolderInvoker<T> : IFolderInvoker
    {
        private readonly IFolder<T> _folder;

        public FolderInvoker(IFolder<T> folder) => _folder = folder;

        public IReadOnlyList<IChangeEvent> Apply(IFrame frame, IWorldClock clock)
        {
            var typed = (Frame<T>)frame;
            return _folder.Apply(typed, clock);
        }
    }
}
