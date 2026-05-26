using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
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
/// Player.log lines from 20:48:30–20:50:24 through the state machine;
/// asserts that both simultaneous Barley plants identify as Barley (the old
/// code reported one as Squash).
/// </summary>
public partial class TwoBarleyRegressionTest
{
    [GeneratedRegex(@"ProcessAddItem\((\w+)\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex AddItemRx();
    [GeneratedRegex(@"ProcessDeleteItem\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DeleteItemRx();
    [GeneratedRegex(@"ProcessSetPetOwner\((\d+),", RegexOptions.CultureInvariant)]
    private static partial Regex SetPetOwnerRx();
    [GeneratedRegex(@"Download appearance loop @(\w+)\(scale=([\d.]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex AppearanceRx();
    [GeneratedRegex(@"ProcessUpdateDescription\((\d+),\s*""([^""]+)"",\s*""([^""]*)"",\s*""([^""]+)"",\s*\w+,\s*""\w+\(Scale=([\d.]+)\)"",\s*\d+\)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateDescRx();
    [GeneratedRegex(@"ProcessUpdateItemCode\((\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex UpdateItemCodeRx();

    private static void Feed(GardenStateMachine sm, string line, DateTime ts)
    {
        Match m;
        if ((m = SetPetOwnerRx().Match(line)).Success) { sm.Apply(new SetPetOwner(ts, m.Groups[1].Value)); return; }
        if ((m = AppearanceRx().Match(line)).Success) { sm.Apply(new AppearanceLoop(ts, m.Groups[1].Value, double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture))); return; }
        if ((m = UpdateDescRx().Match(line)).Success) { sm.Apply(new UpdateDescription(ts, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value, double.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture))); return; }
        if ((m = UpdateItemCodeRx().Match(line)).Success) { sm.Apply(new UpdateItemCode(ts, m.Groups[1].Value)); return; }
        if ((m = AddItemRx().Match(line)).Success) { sm.Apply(new AddItem(ts, m.Groups[2].Value, m.Groups[1].Value)); return; }
        if ((m = DeleteItemRx().Match(line)).Success) { sm.Apply(new DeleteItem(ts, m.Groups[1].Value)); return; }
    }

    [Fact]
    public void LastSquashSeed_FiresDeleteItem_ResolvesPlant()
    {
        // Real Player.log slice for the Squash mis-identification report.
        // Plot 803506 was planted as the player's last Squash seedling; the
        // game emits ProcessDeleteItem (not UpdateItemCode) when the stack
        // reaches zero. Without DeleteItem handling the plot stays "Unknown".
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
            Feed(sm, line, ts);

        sm.Snapshot()["Emraell"]["803506"].CropType.Should().Be("Squash");
    }

    [Fact]
    public void TwoSimultaneousBarleyPlants_BothIdentifyCorrectly()
    {
        var cfg = new InMemoryCropConfigStore();
        var refData = new BarleyOnlyReferenceData();
        var ac = new FakeActiveCharacterService();
        ac.SetActiveCharacter("Hits", "");
        var sm = new GardenStateMachine(cfg, referenceData: refData, activeChar: ac);

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
            Feed(sm, line, ts);

        var plots = sm.Snapshot()["Hits"];
        plots.Should().ContainKey("590342");
        plots.Should().ContainKey("590364");
        plots["590342"].CropType.Should().Be("Barley", "first plant must identify as Barley (was Squash under the bug)");
        plots["590364"].CropType.Should().Be("Barley", "second plant must identify as Barley");
    }

    [Fact]
    public void SeedAddItemBeforePlant_StateMachine_ResolvesPlant()
    {
        // State-machine-only guard for the seed→crop resolution invariant: an
        // AddItem for the seed instance, applied to the state machine before
        // the SetPetOwner / ProcessUpdateItemCode pair, must populate the
        // id→crop ledger so the subsequent plant resolves. This pins a
        // GardenStateMachine input property (the in-memory ledger semantics),
        // NOT the bus-delivery path; see GardenIngestionServiceBusDeliveryTests
        // for the consumer-side bus integration guard added under #725.
        var cfg = new InMemoryCropConfigStore();
        var ac = new FakeActiveCharacterService();
        ac.SetActiveCharacter("Hits", "");
        var sm = new GardenStateMachine(cfg, referenceData: new BarleyOnlyReferenceData(), activeChar: ac);

        sm.Apply(new AddItem(
            new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc),
            ItemId: "86940428",
            ItemName: "BarleySeeds"));

        Feed(sm, "[20:50:22] LocalPlayer: ProcessSetPetOwner(590342, 588755, PassiveFollow)",
            new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc));
        Feed(sm, "[20:50:22] LocalPlayer: ProcessUpdateItemCode(86940428, 796683, True)",
            new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc));

        sm.Snapshot()["Hits"]["590342"].CropType
            .Should().Be("Barley", "the pre-plant AddItem populates the id→crop ledger so plant-resolve maps 86940428→Barley");
    }

    private sealed class BarleyOnlyReferenceData : IReferenceDataService
    {
        public IReadOnlyList<string> Keys { get; } = ["items"];
        private static readonly Item _barley = new()
        {
            Id = 10251, Name = "Barley Seeds", InternalName = "BarleySeeds",
            MaxStackSize = 100, IconId = 0, Keywords = [],
        };
        public IReadOnlyDictionary<long, Item> Items { get; } = new Dictionary<long, Item>
        {
            [10251L] = _barley,
        };
        public IReadOnlyDictionary<string, Item> ItemsByInternalName { get; } = new Dictionary<string, Item>(StringComparer.Ordinal)
        {
            ["BarleySeeds"] = _barley,
        };
        public ItemKeywordIndex KeywordIndex => new(Items);
        public IReadOnlyDictionary<string, Recipe> Recipes { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, Recipe> RecipesByInternalName { get; } = new Dictionary<string, Recipe>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, AreaEntry> Areas { get; } = new Dictionary<string, AreaEntry>(StringComparer.Ordinal);
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> Quests { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, Mithril.Reference.Models.Quests.Quest> QuestsByInternalName { get; } = new Dictionary<string, Mithril.Reference.Models.Quests.Quest>();
        public IReadOnlyDictionary<string, string> Strings { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
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
