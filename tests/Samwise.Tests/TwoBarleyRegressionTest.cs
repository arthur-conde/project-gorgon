using System.Text.RegularExpressions;
using FluentAssertions;
using Mithril.Shared.Reference;
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
    private static readonly Regex AddItemRx = new(@"ProcessAddItem\((\w+)\((\d+)\)", RegexOptions.CultureInvariant);
    private static readonly Regex DeleteItemRx = new(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Mirror of <c>GardenIngestionService</c>'s log handling: parser-emitted
    /// events go straight in; ProcessAddItem/ProcessDeleteItem are sourced via
    /// IInventoryService in production, so we simulate that synthesis here so
    /// the test exercises the same state-machine inputs end-to-end.
    /// </summary>
    private static void Feed(GardenStateMachine sm, GardenLogParser parser, string line, DateTime ts)
    {
        var evt = parser.TryParse(line, ts);
        if (evt is GardenEvent ge) { sm.Apply(ge); return; }

        var add = AddItemRx.Match(line);
        if (add.Success) { sm.Apply(new AddItem(ts, add.Groups[2].Value, add.Groups[1].Value)); return; }

        var del = DeleteItemRx.Match(line);
        if (del.Success) sm.Apply(new DeleteItem(ts, del.Groups[1].Value));
    }

    [Fact]
    public void LastSquashSeed_FiresDeleteItem_ResolvesPlant()
    {
        // Real Player.log slice for the Squash mis-identification report.
        // Plot 803506 was planted as the player's last Squash seedling; the
        // game emits ProcessDeleteItem (not UpdateItemCode) when the stack
        // reaches zero. Without DeleteItem handling the plot stays "Unknown".
        var parser = new GardenLogParser();
        var cfg = new InMemoryCropConfigStore();
        var ac = new FakeActiveCharacterService();
        ac.SetActiveCharacter("Emraell", "");
        var sm = new GardenStateMachine(cfg, referenceData: new BarleyOnlyReferenceData(), activeChar: ac);

        var logLines = new (string line, DateTime ts)[]
        {
            ("[01:08:48] LocalPlayer: ProcessAddPlayer(123, 999, \"PlayerModelDescriptor\", \"Emraell\", 0)", new DateTime(2026, 4, 16, 1, 8, 48, DateTimeKind.Utc)),
            ("[01:08:48] LocalPlayer: ProcessAddItem(SquashSeedling(93102594), -1, True)",       new DateTime(2026, 4, 16, 1, 8, 48, DateTimeKind.Utc)),
            ("[01:09:44] LocalPlayer: ProcessSetPetOwner(803506, 791931, PassiveFollow)",        new DateTime(2026, 4, 16, 1, 9, 44, DateTimeKind.Utc)),
            ("[01:09:44] LocalPlayer: ProcessSetPetCombatMode(803506, AttackMyTargetsFollow)",   new DateTime(2026, 4, 16, 1, 9, 44, DateTimeKind.Utc)),
            ("[01:09:44] LocalPlayer: ProcessDeleteItem(93102594)",                              new DateTime(2026, 4, 16, 1, 9, 44, DateTimeKind.Utc)),
            ("[01:09:44] Download appearance loop @Squash(scale=0.4) is done",                   new DateTime(2026, 4, 16, 1, 9, 44, DateTimeKind.Utc)),
        };

        foreach (var (line, ts) in logLines)
            Feed(sm, parser, line, ts);

        sm.Snapshot()["Emraell"]["803506"].CropType.Should().Be("Squash");
    }

    [Fact]
    public void TwoSimultaneousBarleyPlants_BothIdentifyCorrectly()
    {
        var parser = new GardenLogParser();
        var cfg = new InMemoryCropConfigStore();
        var refData = new BarleyOnlyReferenceData();
        var ac = new FakeActiveCharacterService();
        ac.SetActiveCharacter("Hits", "");
        var sm = new GardenStateMachine(cfg, referenceData: refData, activeChar: ac);

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
            Feed(sm, parser, line, ts);

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
            [10251L] = new(10251, "Barley Seeds", "BarleySeeds", 100, 0, []),
        };
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName { get; } = new Dictionary<string, ItemEntry>(StringComparer.Ordinal)
        {
            ["BarleySeeds"] = new(10251, "Barley Seeds", "BarleySeeds", 100, 0, []),
        };
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
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
