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
