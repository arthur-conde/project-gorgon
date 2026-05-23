using Mithril.WorldSim;
using Mithril.WorldSim.Player;
using Samwise.Config;
using Samwise.State;

namespace Samwise.Tests;

internal sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; private set; }
    public FakeTime(DateTime utc) { Now = new DateTimeOffset(utc, TimeSpan.Zero); }
    public override DateTimeOffset GetUtcNow() => Now;
    public void Advance(TimeSpan ts) => Now += ts;
}

/// <summary>
/// Fake <see cref="IWorldClock"/> that advances when callers explicitly set
/// <see cref="Now"/>. The production clock is driven by frame timestamps from
/// the L1 source-stream; the test stand-in just hands the value to consumers.
/// </summary>
internal sealed class FakeWorldClock : IWorldClock
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.MinValue;
    public long Frame { get; set; }
    public WorldMode Mode { get; set; } = WorldMode.Live;
}

/// <summary>
/// Fake <see cref="IPlayerWorld"/> exposing a <see cref="FakeWorldClock"/>
/// plus a synthetic-publish <see cref="TestEventBus"/> for tests that drive
/// PlayerWorld bus frames directly (post-#725 the Samwise inventory subscription
/// reads <see cref="PlayerInventoryAdded"/> / <see cref="PlayerInventoryRemoved"/>
/// off this bus). The register methods are unused by the in-tree tests —
/// folder / composer registration lives in <c>Mithril.WorldSim.Player.Tests</c>.
/// </summary>
internal sealed class FakePlayerWorld : IPlayerWorld
{
    public FakeWorldClock WorldClock { get; } = new();
    public TestEventBus TestBus { get; } = new();

    public IWorldClock Clock => WorldClock;
    public IWorldEventBus Bus => TestBus;

    public void RegisterProducer<T>(IFrameProducer<T> producer) =>
        throw new NotSupportedException("FakePlayerWorld is read-only.");
    public void RegisterFolder<T>(IFolder<T> folder) =>
        throw new NotSupportedException("FakePlayerWorld is read-only.");
    public void RegisterComposer(IComposer composer) =>
        throw new NotSupportedException("FakePlayerWorld is read-only.");
    public Task StartMerger(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Headless typed pub-sub bus for tests — mirrors the real
/// <c>WorldEventBus</c> shape (subscribers see <see cref="Frame{T}"/>
/// payloads on the publishing thread) without the per-type registration
/// machinery. The test calls <see cref="Publish{T}(DateTimeOffset, T)"/>
/// with a payload and the bus wraps it into a <see cref="Frame{T}"/>.
/// </summary>
internal sealed class TestEventBus : IWorldEventBus
{
    private readonly object _lock = new();
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();

    public IDisposable Subscribe<T>(Action<Frame<T>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list))
                _handlers[typeof(T)] = list = new List<Delegate>();
            list.Add(handler);
            return new Sub(this, typeof(T), handler);
        }
    }

    public void Publish<T>(DateTimeOffset timestamp, T payload)
    {
        List<Delegate>? snap;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var list)) return;
            snap = list.ToList();
        }
        var frame = new Frame<T>(timestamp, payload);
        foreach (var h in snap) ((Action<Frame<T>>)h)(frame);
    }

    public int SubscriberCountFor(Type t)
    {
        lock (_lock) return _handlers.TryGetValue(t, out var list) ? list.Count : 0;
    }

    private sealed class Sub(TestEventBus o, Type t, Delegate h) : IDisposable
    {
        public void Dispose()
        {
            lock (o._lock) { if (o._handlers.TryGetValue(t, out var list)) list.Remove(h); }
        }
    }
}

internal sealed class InMemoryCropConfig : ICropConfigStore
{
    public CropConfig Current { get; }
    public event EventHandler? Reloaded;
    public Task ReloadAsync(CancellationToken ct = default) { Reloaded?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    public InMemoryCropConfig()
    {
        Current = new CropConfig
        {
            SlotFamilies = new()
            {
                ["Carrot"] = new() { Max = 2 },
                ["Onion"] = new() { Max = 2 },
                ["Cotton"] = new() { Max = 5 },
                ["Flowers"] = new() { Max = 3 },
            },
            Crops = new()
            {
                ["Carrot"] = new() { SlotFamily = "Carrot", GrowthSeconds = 175 },
                ["Onion"] = new() { SlotFamily = "Onion", GrowthSeconds = 50 },
                ["Squash"] = new() { SlotFamily = "Onion", GrowthSeconds = 170 },
                ["Violet"] = new() { SlotFamily = "Flowers", GrowthSeconds = 110 },
                ["Pansy"] = new() { SlotFamily = "Flowers", GrowthSeconds = 140 },
                ["Cotton Plant"] = new() { SlotFamily = "Cotton", GrowthSeconds = 150 },
                ["Barley"] = new() { SlotFamily = "Carrot", GrowthSeconds = 150 },
            },
        };
    }
}
