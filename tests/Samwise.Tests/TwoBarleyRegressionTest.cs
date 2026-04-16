using FluentAssertions;
using Gorgon.Shared.Reference;
using Samwise.Config;
using Samwise.Parsing;
using Samwise.State;
using Xunit;

namespace Samwise.Tests;

/// <summary>
/// End-to-end regression for the original bug report. Replays the literal
/// Player.log lines from 20:48:30–20:50:24 through the real parser and the
/// real state machine; asserts that both simultaneous Barley plants identify
/// as Barley (the old code reported one as Squash).
/// </summary>
public class TwoBarleyRegressionTest
{
    [Fact]
    public void TwoSimultaneousBarleyPlants_BothIdentifyCorrectly()
    {
        var parser = new GardenLogParser();
        var cfg = new InMemoryCropConfigStore();
        var refData = new BarleyOnlyReferenceData();
        var sm = new GardenStateMachine(cfg, referenceData: refData);

        // Real Player.log slice covering the seed AddItem + the two plants.
        var logLines = new[]
        {
            ("[18:38:42] LocalPlayer: ProcessAddPlayer(123, 999, \"PlayerModelDescriptor\", \"Hits\", 0)", new DateTime(2026, 4, 15, 18, 38, 42, DateTimeKind.Utc)),
            ("[20:48:30] LocalPlayer: ProcessAddItem(BarleySeeds(86940428), -1, False)",  new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc)),
            ("[20:50:21] ProcessUpdateDescription(587524, \"Ripe Squash\", \"\", \"Harvest Squash\", UseItem, \"Squash(Scale=1)\", 0)",  new DateTime(2026, 4, 15, 20, 50, 21, DateTimeKind.Utc)),
            ("[20:50:21] Download appearance loop @Squash(scale=1) is done",              new DateTime(2026, 4, 15, 20, 50, 21, DateTimeKind.Utc)),
            ("[20:50:22] LocalPlayer: ProcessSetPetOwner(590342, 588755, PassiveFollow)", new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc)),
            ("[20:50:22] LocalPlayer: ProcessUpdateItemCode(86940428, 796683, True)",     new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc)),
            ("[20:50:23] Download appearance loop @Barley(scale=0.5) is waiting on Appearance barley", new DateTime(2026, 4, 15, 20, 50, 23, DateTimeKind.Utc)),
            ("[20:50:23] Download appearance loop @Barley(scale=0.5) is done",            new DateTime(2026, 4, 15, 20, 50, 23, DateTimeKind.Utc)),
            ("[20:50:23] LocalPlayer: ProcessSetPetOwner(590364, 588755, PassiveFollow)", new DateTime(2026, 4, 15, 20, 50, 23, DateTimeKind.Utc)),
            ("[20:50:23] LocalPlayer: ProcessUpdateItemCode(86940428, 731147, True)",     new DateTime(2026, 4, 15, 20, 50, 23, DateTimeKind.Utc)),
            ("[20:50:24] Download appearance loop @Barley(scale=0.5) is done",            new DateTime(2026, 4, 15, 20, 50, 24, DateTimeKind.Utc)),
        };

        foreach (var (line, ts) in logLines)
        {
            var evt = parser.TryParse(line, ts);
            if (evt is GardenEvent ge) sm.Apply(ge);
        }

        var plots = sm.Snapshot()["Hits"];
        plots.Should().ContainKey("590342");
        plots.Should().ContainKey("590364");
        plots["590342"].CropType.Should().Be("Barley", "first plant must identify as Barley (was Squash under the bug)");
        plots["590364"].CropType.Should().Be("Barley", "second plant must identify as Barley");
    }

    private sealed class BarleyOnlyReferenceData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>
        {
            [10251L] = new(10251, "Barley Seeds", "BarleySeeds", 100, 0),
        };
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>(StringComparer.Ordinal)
        {
            ["BarleySeeds"] = new(10251, "Barley Seeds", "BarleySeeds", 100, 0),
        };
        public ReferenceFileSnapshot GetSnapshot(string key)
            => new("items", ReferenceFileSource.Bundled, "test", null, 1);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }

    private sealed class InMemoryCropConfigStore : ICropConfigStore
    {
        public CropConfig Current { get; } = new()
        {
            SlotFamilies = new() { ["Grass"] = new() { Max = 8 }, ["Squash"] = new() { Max = 2 } },
            Crops = new()
            {
                ["Barley"] = new() { SlotFamily = "Grass", GrowthSeconds = 150 },
                ["Squash"] = new() { SlotFamily = "Squash", GrowthSeconds = 170 },
            },
        };
        public event EventHandler? Reloaded;
        public Task ReloadAsync(CancellationToken ct = default) { Reloaded?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
    }
}
