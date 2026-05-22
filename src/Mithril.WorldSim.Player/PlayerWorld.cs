using Mithril.WorldSim.Player.Internal;

namespace Mithril.WorldSim.Player;

/// <summary>
/// Concrete <see cref="IPlayerWorld"/> implementation (issue #616 — Phase 0
/// shell). Owns the merger over registered producers, the clock, the bus, and
/// the dispatch graph. Folders and composers register here but the shell
/// itself wires no specific folders — those land via per-folder Phase 1+
/// migration issues.
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
public sealed class PlayerWorld : IPlayerWorld, IAsyncDisposable
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

        // If no mode-aware producers were registered, the world is implicitly
        // live from t=0 (nothing to drain). Otherwise it starts Replaying
        // and flips once every mode-aware ReachedLive task completes.
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
            // Empty world — nothing to dispatch. Still useful for tests
            // exercising the bus / clock surface in isolation.
            UpdateModeIfReady();
            return;
        }

        var states = _producers
            .Select(p => new ProducerRuntimeState(p, p.Adapter.GetEnumerator(ct)))
            .ToList();

        try
        {
            // Prime each producer.
            foreach (var s in states)
            {
                s.PendingFetch = s.Enumerator.MoveNextAsync(ct);
            }

            while (!ct.IsCancellationRequested)
            {
                // Wait until every non-exhausted producer has a head frame (or
                // has gone exhausted). For multi-producer correctness we
                // cannot dispatch until we know the minimum-timestamp frame
                // across all producers — see design notebook §Determinism +
                // principle 1.
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

                // Re-evaluate mode now that we've potentially seen the first
                // live frame from one of the producers (mode-aware producers
                // signal ReachedLive *before* yielding the first live frame).
                UpdateModeIfReady();

                // Find the min by (Timestamp, Priority, RegisteredIndex).
                ProducerRuntimeState? winner = null;
                foreach (var s in states)
                {
                    if (!s.HasHead) continue;
                    if (winner is null || Compare(s, winner) < 0)
                    {
                        winner = s;
                    }
                }

                if (winner is null)
                {
                    // All producers exhausted.
                    break;
                }

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
            if (p.ReachedLive is { IsCompleted: false })
            {
                return;
            }
        }

        var transitionAt = _clock.Now;
        _clock.SetMode(WorldMode.Live);

        // Only emit ModeChanged if the world actually experienced a Replaying
        // phase — i.e., at least one frame was applied while Mode == Replaying.
        // If we flip before any frame applies (empty L1 seed: producer signals
        // ReachedLive on the first envelope, which is itself live; or every
        // mode-aware producer was already complete at registration time),
        // there is no transition to report. Consumers read Clock.Mode for
        // initial state and subscribe to ModeChanged only for actual
        // transitions — symmetric with the no-mode-aware-producers branch in
        // StartAsync which sets Live up front without emitting.
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
            // No folder registered for this payload type — the world's
            // Phase 0 shell expects this for nearly every payload type.
            // Frames flow through the clock without state mutation; mode
            // tracking still operates. Per-folder migrations (Phase 1+)
            // attach folders to specific payload types.
            return;
        }

        var changes = folder.Apply(frame, _clock);
        if (changes.Count == 0) return;

        var workQueue = new Queue<object>(changes.Count);
        foreach (var c in changes)
        {
            // Surface folder change events on the bus AS WELL AS routing them
            // to intra-world composers. Single-world consumers (per design
            // notebook principle 4 — "modules needing only one source's
            // state may subscribe directly to that world's bus") subscribe
            // to the concrete change-event type; the bus wraps the runtime
            // type into Frame<T> via a cached compiled delegate, so no
            // pass-through composer is needed per folder. Composer emissions
            // continue to publish via Bus.Publish in the resolution loop
            // below; both flow through the same subscriber pipeline.
            _bus.PublishChangeEvent(c, frame.Timestamp);
            workQueue.Enqueue(c);
        }

        // Resolve composers in BFS order. The IComposer contract guarantees
        // no merger re-entry, so this terminates whenever no composer emits
        // a new IFrame for a type the queue can still match.
        while (workQueue.Count > 0)
        {
            var payload = workQueue.Dequeue();
            var payloadType = payload.GetType();
            if (!_composersByType.TryGetValue(payloadType, out var composers))
            {
                continue;
            }

            // Snapshot — composer.Observe must not mutate the dispatch graph
            // (registrations close at StartAsync), so a direct iteration is
            // safe; the snapshot guards against accidental future changes
            // to that invariant.
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
    // Boxes the typed IAsyncEnumerable<Frame<T>> to a non-generic enumerator
    // surface so the merger can hold a heterogeneous producer list without
    // the merger becoming generic over every payload type.

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
                // ct is honoured by the inner async enumerator (subscribed
                // with the same token in GetEnumerator above); duplicate
                // wiring here would risk a double-throw on cancellation.
                _ = ct;
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
    // Boxes the typed IFolder<T>.Apply behind a non-generic interface so
    // _folders[type].Apply(frame, clock) works without per-type generic
    // dispatch on the hot path.

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
            // The merger only routes a frame to its registered folder via
            // exact PayloadType match (DispatchFrame → _folders[frame.PayloadType]),
            // so this cast is structurally guaranteed and a mismatch indicates
            // a programmer error (e.g., two folders sharing a type slot,
            // which RegisterFolder also forbids).
            var typed = (Frame<T>)frame;
            return _folder.Apply(typed, clock);
        }
    }
}
