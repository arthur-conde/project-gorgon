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
/// Fake <see cref="IPlayerWorld"/> exposing a <see cref="FakeWorldClock"/>;
/// the bus / register methods are unused by the state-decision sites under
/// test (#609 — the migration only reads <see cref="IWorldClock.Now"/>).
/// </summary>
internal sealed class FakePlayerWorld : IPlayerWorld
{
    public FakeWorldClock WorldClock { get; } = new();

    public IWorldClock Clock => WorldClock;

    public IWorldEventBus Bus => throw new NotSupportedException(
        "FakePlayerWorld exposes only Clock; folder / composer / bus surfaces aren't needed for clock-migration tests.");

    public void RegisterProducer<T>(IFrameProducer<T> producer) =>
        throw new NotSupportedException("FakePlayerWorld is read-only.");
    public void RegisterFolder<T>(IFolder<T> folder) =>
        throw new NotSupportedException("FakePlayerWorld is read-only.");
    public void RegisterComposer(IComposer composer) =>
        throw new NotSupportedException("FakePlayerWorld is read-only.");
    public Task StartMerger(CancellationToken ct) => Task.CompletedTask;
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
