using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mithril.Shell.DependencyInjection;
using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Xunit;

namespace Mithril.Shell.Tests;

/// <summary>
/// #696 Call 2 — the merger-vs-registration race fix. These tests exercise the
/// trailing-registration invariant: a worker's bus subscription, attached during
/// the chain's hosted-service <c>StartAsync</c> phase, MUST be in place before
/// any world's merger drains its producers.
///
/// <para>The invariant is enforced structurally by appending
/// <c>AddWorldMergerStart()</c> as the LAST registration inside
/// <c>AddMithrilApp</c>. The hosted-services contract guarantees
/// registration-order <c>StartAsync</c> dispatch, so the trailing
/// <c>WorldMergerStartHostedService</c> runs after every other hosted service
/// has completed its registration work. Its <c>StartAsync</c> fires
/// <see cref="IWorld.StartMerger"/> on each registered world WITHOUT awaiting
/// completion (the returned task is the long-running drain, owned by the
/// hosted service until <c>StopAsync</c>).</para>
///
/// <para>These tests use friend-assembly access (via the <c>InternalsVisibleTo</c>
/// declared on <c>Mithril.Shell</c>) to call <c>AddWorldMergerStart()</c>
/// directly against a partial stack — that capability is itself part of the
/// #696 acceptance scope: it lets tests compose
/// <c>AddPlayerWorld() + AddWorldMergerStart()</c> for "single world with a
/// live merger" scenarios that don't drag in the entire shell graph.</para>
/// </summary>
public sealed class WorldMergerStartCompositionTests
{
    private static DateTimeOffset Ts(int sec) => new(2026, 1, 1, 12, 0, sec, TimeSpan.Zero);

    /// <summary>
    /// Friend-assembly smoke test: <c>AddWorldMergerStart()</c> appends a
    /// hosted service to the collection. If the friend-assembly access is
    /// broken, this file doesn't even compile.
    /// </summary>
    [Fact]
    public void AddWorldMergerStart_appends_hosted_service_descriptor()
    {
        var services = new ServiceCollection();
        var before = services.Count(d => d.ServiceType == typeof(IHostedService));

        services.AddWorldMergerStart();

        var after = services.Count(d => d.ServiceType == typeof(IHostedService));
        after.Should().Be(before + 1, "AddWorldMergerStart adds exactly one hosted-service descriptor");
    }

    /// <summary>
    /// Behavioural ordering test: when <c>AddWorldMergerStart</c> trails another
    /// hosted service in the registration order, the merger drain must not begin
    /// until that earlier hosted service's <c>StartAsync</c> has returned.
    ///
    /// <para>Verified by attaching a bus subscription from the earlier hosted
    /// service AND signalling a TaskCompletionSource at the end of its
    /// <c>StartAsync</c>. The producer's first <c>MoveNextAsync</c> asserts the
    /// TCS is already completed — i.e., the merger genuinely waited.</para>
    /// </summary>
    [Fact]
    public async Task Merger_does_not_drain_producers_before_earlier_hosted_services_complete_StartAsync()
    {
        var subscriberStarted = new TaskCompletionSource();
        var observed = new List<string>();

        var stubFrames = new[]
        {
            new Frame<string>(Ts(1), "f1"),
            new Frame<string>(Ts(2), "f2"),
        };
        var producer = new OrderingStubProducer<string>(subscriberStarted.Task, stubFrames);

        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(new RecordingFolder<string>(observed));

        var services = new ServiceCollection();
        services.AddSingleton(world);
        services.AddSingleton<IWorld>(sp => sp.GetRequiredService<PlayerWorld>());
        // Earlier hosted service: subscribes to the world's bus (the "attach a
        // subscriber before frames flow" contract) and signals when its
        // StartAsync returns. Registered BEFORE AddWorldMergerStart so the
        // hosted-service runner invokes its StartAsync first.
        services.AddSingleton(new BusSubscriberHosted(world, subscriberStarted));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BusSubscriberHosted>());
        services.AddWorldMergerStart();

        await using var sp = services.BuildServiceProvider();
        var lifetime = new TestHostLifetime(sp);
        await lifetime.StartAsync(CancellationToken.None);

        // Drain happens on a background task — give the merger a brief window
        // to see all the stub frames.
        await producer.AllFramesConsumed.WaitAsync(TimeSpan.FromSeconds(5));

        await lifetime.StopAsync(CancellationToken.None);

        producer.SubscriberWasReadyAtFirstFetch.Should().BeTrue(
            "the producer must not be asked to enumerate until the earlier hosted service's StartAsync has completed");
        observed.Should().Equal("f1", "f2");
    }

    /// <summary>
    /// Subscriber test: a hosted service registered earlier in the chain
    /// subscribes to the world's bus DURING its StartAsync. The merger then
    /// drains the producer; the subscriber receives every frame.
    /// </summary>
    [Fact]
    public async Task Bus_subscriber_attached_during_chain_receives_frames_from_session_start_replay()
    {
        var subscriberStarted = new TaskCompletionSource();
        var observed = new List<string>();

        var stubFrames = new[]
        {
            new Frame<string>(Ts(10), "replay-1"),
            new Frame<string>(Ts(11), "replay-2"),
            new Frame<string>(Ts(12), "replay-3"),
        };
        var producer = new OrderingStubProducer<string>(subscriberStarted.Task, stubFrames);

        var world = new PlayerWorld();
        world.RegisterProducer(producer);
        world.RegisterFolder(new RecordingFolder<string>(observed));

        var receivedOnBus = new List<string>();
        var services = new ServiceCollection();
        services.AddSingleton(world);
        services.AddSingleton<IWorld>(sp => sp.GetRequiredService<PlayerWorld>());
        services.AddSingleton(new BusSubscriberHosted(world, subscriberStarted, receivedOnBus));
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<BusSubscriberHosted>());
        services.AddWorldMergerStart();

        await using var sp = services.BuildServiceProvider();
        var lifetime = new TestHostLifetime(sp);
        await lifetime.StartAsync(CancellationToken.None);
        await producer.AllFramesConsumed.WaitAsync(TimeSpan.FromSeconds(5));
        await lifetime.StopAsync(CancellationToken.None);

        // The bus subscription attached during the chain's StartAsync phase
        // must see every replay frame.
        receivedOnBus.Should().Equal("replay-1", "replay-2", "replay-3");
    }

    // ── Stubs ────────────────────────────────────────────────────────────

    /// <summary>
    /// Producer that exposes its first-fetch readiness signal so a test can
    /// assert "did this producer wait for the earlier hosted services."
    /// </summary>
    private sealed class OrderingStubProducer<T> : IFrameProducer<T>
    {
        private readonly Task _subscriberStarted;
        private readonly Frame<T>[] _frames;
        private readonly TaskCompletionSource _allConsumed = new();

        public OrderingStubProducer(Task subscriberStarted, params Frame<T>[] frames)
        {
            _subscriberStarted = subscriberStarted;
            _frames = frames;
        }

        public int Priority => 0;
        public bool SubscriberWasReadyAtFirstFetch { get; private set; }
        public Task AllFramesConsumed => _allConsumed.Task;

        public async IAsyncEnumerable<Frame<T>> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            // First fetch — snapshot whether the earlier hosted service's
            // StartAsync has completed by the time the merger asks us to
            // enumerate. This is the load-bearing assertion: if the merger
            // raced ahead of the chain's StartAsync, this would be false.
            SubscriberWasReadyAtFirstFetch = _subscriberStarted.IsCompletedSuccessfully;

            for (int i = 0; i < _frames.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return _frames[i];
            }
            _allConsumed.TrySetResult();
        }
    }

    /// <summary>
    /// Hosted service that subscribes to the world's bus during its
    /// <c>StartAsync</c> and signals a TCS so the producer can observe its
    /// completion. If <paramref name="observed"/> is supplied, every change
    /// event flowing on the bus is recorded into it.
    /// </summary>
    private sealed class BusSubscriberHosted : IHostedService
    {
        private readonly PlayerWorld _world;
        private readonly TaskCompletionSource _started;
        private readonly List<string>? _observed;
        private IDisposable? _sub;

        public BusSubscriberHosted(PlayerWorld world, TaskCompletionSource started, List<string>? observed = null)
        {
            _world = world;
            _started = started;
            _observed = observed;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (_observed is not null)
            {
                _sub = _world.Bus.Subscribe<StringFolderChange>(f =>
                {
                    lock (_observed) _observed.Add(f.Payload.Value);
                });
            }
            _started.TrySetResult();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sub?.Dispose();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Folder that emits a typed change event for every applied frame, so the
    /// bus has something concrete to deliver to the BusSubscriberHosted's
    /// subscription. Records the payload into the supplied list for the
    /// non-bus ordering assertion.
    /// </summary>
    private sealed class RecordingFolder<T> : IFolder<T>
    {
        private readonly List<string> _applied;
        public RecordingFolder(List<string> applied) => _applied = applied;

        public IReadOnlyList<IChangeEvent> Apply(Frame<T> frame, IWorldClock clock)
        {
            lock (_applied) _applied.Add(frame.Payload?.ToString() ?? "");
            return new IChangeEvent[] { new StringFolderChange(frame.Payload?.ToString() ?? "") };
        }
    }

    private sealed record StringFolderChange(string Value) : IChangeEvent;

    /// <summary>
    /// Minimal hosted-services runner — calls <c>StartAsync</c> on every
    /// registered <see cref="IHostedService"/> in registration order, awaits
    /// each completion, then mirrors the symmetric <c>StopAsync</c> ordering
    /// during teardown. Lets these tests exercise the hosted-services contract
    /// without standing up a full generic <c>IHost</c> + its scopes/lifetime
    /// machinery.
    /// </summary>
    private sealed class TestHostLifetime
    {
        private readonly IReadOnlyList<IHostedService> _services;

        public TestHostLifetime(IServiceProvider sp)
            => _services = sp.GetServices<IHostedService>().ToArray();

        public async Task StartAsync(CancellationToken ct)
        {
            foreach (var s in _services)
                await s.StartAsync(ct).ConfigureAwait(false);
        }

        public async Task StopAsync(CancellationToken ct)
        {
            for (int i = _services.Count - 1; i >= 0; i--)
            {
                try { await _services[i].StopAsync(ct).ConfigureAwait(false); }
                catch { /* shutdown is best-effort in this fixture */ }
            }
        }
    }
}
