using Mithril.Reference.Models.Items;
using Mithril.Reference.Models.Recipes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FluentAssertions;
using Mithril.GameState.Inventory;
using Mithril.Shared.Logging;
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

    [Fact]
    public async Task SubscribeAfterSeedAdd_StillResolvesPlant()
    {
        // Direct repro for issue #7: the seed AddItem fires during PlayerLogStream's
        // session-replay flush, BEFORE the gated GardenIngestionService attaches its
        // handler. Under the old `event ItemAdded` design, that AddItem was lost and
        // the plant later landed with CropType=null → "Unknown" in the UI.
        // Under the new Subscribe(replay-on-attach) contract, the AddItem is replayed
        // synchronously when GardenIngestionService subscribes, so the plant resolves.
        var parser = new GardenLogParser();
        var cfg = new InMemoryCropConfigStore();
        var ac = new FakeActiveCharacterService();
        ac.SetActiveCharacter("Hits", "");
        var sm = new GardenStateMachine(cfg, referenceData: new BarleyOnlyReferenceData(), activeChar: ac);

        // Drive the real InventoryService against a scripted L1 driver.
        var stream = new ScriptedStream();
        var inv = new InventoryService(stream);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = inv.StartAsync(cts.Token);

        // Push the seed AddItem BEFORE Samwise subscribes — simulating the gate
        // race where InventoryService is up but GardenIngestionService is still
        // doing LoadAllAsync.
        stream.Push("[20:48:30] LocalPlayer: ProcessAddItem(BarleySeeds(86940428), -1, False)",
            new DateTime(2026, 4, 15, 20, 48, 30, DateTimeKind.Utc));
        await stream.WaitForDrainAsync(cts.Token);

        // Now subscribe — replay must deliver the seed AddItem retroactively.
        using var sub = inv.Subscribe(evt =>
        {
            var idStr = evt.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            GardenEvent? ge = evt.Kind switch
            {
                InventoryEventKind.Added => new AddItem(evt.Timestamp, idStr, evt.InternalName),
                InventoryEventKind.Deleted => new DeleteItem(evt.Timestamp, idStr),
                _ => null,
            };
            if (ge is not null) sm.Apply(ge);
        });

        // Now plant: SetPetOwner + ProcessUpdateItemCode (parser-driven path).
        Feed(sm, parser, "[20:50:22] LocalPlayer: ProcessSetPetOwner(590342, 588755, PassiveFollow)",
            new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc));
        Feed(sm, parser, "[20:50:22] LocalPlayer: ProcessUpdateItemCode(86940428, 796683, True)",
            new DateTime(2026, 4, 15, 20, 50, 22, DateTimeKind.Utc));

        sm.Snapshot()["Hits"]["590342"].CropType
            .Should().Be("Barley", "Subscribe replay must deliver the seed AddItem so plant-resolve can map id→Barley");

        await cts.CancelAsync();
        try { await inv.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    /// <summary>
    /// Minimal <see cref="ILogStreamDriver"/> fake for Samwise tests after the
    /// L1 migration (#565): only the <see cref="LocalPlayerLogLine"/> pipe is
    /// exercised here (no chat). Strips the standard
    /// <c>[HH:MM:SS] LocalPlayer: </c> envelope from pushed Player.log shapes
    /// so the test reads like the pre-L1 raw-line stream did.
    /// </summary>
    private sealed class ScriptedStream : ILogStreamDriver
    {
        private const int TsPrefixLen = 11;             // length of "[HH:MM:SS] "
        private const string ActorToken = "LocalPlayer: ";

        private readonly Channel<LocalPlayerLogLine> _channel =
            Channel.CreateUnbounded<LocalPlayerLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ILogSubscription Subscribe<T>(
            Func<LogEnvelope<T>, ValueTask> handler,
            LogSubscriptionOptions? options = null) where T : class
        {
            if (typeof(T) == typeof(LocalPlayerLogLine))
            {
                var typed = (Func<LogEnvelope<LocalPlayerLogLine>, ValueTask>)(object)handler;
                var cts = new CancellationTokenSource();
                _ = Task.Run(() => PumpAsync(typed, cts.Token));
                // Fire-and-forget cancel on dispose. Any in-flight handler invocation
                // unwinds on its own thread; tests don't need to wait for it.
                return new Sub(() => { try { cts.Cancel(); } catch { } });
            }
            // Chat / other pipes: accept-and-ignore so InventoryService's second
            // Subscribe<RawLogLine> call doesn't blow up.
            return new Sub(() => { });
        }

        public void Push(string line, DateTime? timestamp = null)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(new LocalPlayerLogLine(
                new DateTimeOffset(timestamp ?? DateTime.UtcNow, TimeSpan.Zero),
                StripEnvelope(line), 0, 0));
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);

        private async Task PumpAsync(
            Func<LogEnvelope<LocalPlayerLogLine>, ValueTask> handler, CancellationToken ct)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var line))
                    {
                        try { await handler(new LogEnvelope<LocalPlayerLogLine>(line, IsReplay: false)).ConfigureAwait(false); }
                        catch { /* mirror driver containment */ }
                        finally
                        {
                            if (Interlocked.Decrement(ref _pending) == 0) _drained.TrySetResult();
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* expected on dispose */ }
        }

        private static string StripEnvelope(string line)
        {
            var idx = 0;
            if (line.Length > TsPrefixLen
                && line[0] == '[' && line[3] == ':' && line[6] == ':' && line[9] == ']')
                idx = TsPrefixLen;
            if (idx + ActorToken.Length <= line.Length
                && line.IndexOf(ActorToken, idx, StringComparison.Ordinal) == idx)
                idx += ActorToken.Length;
            return idx == 0 ? line : line.Substring(idx);
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class Sub : ILogSubscription
        {
            private readonly Action _onDispose;
            public Sub(Action onDispose) { _onDispose = onDispose; }
            public string Id => "samwise#scripted";
            public LogSubscriptionDiagnostics Diagnostics =>
                new(0, 0, 0, 0, 0, LogSubscriptionState.Healthy);
            public event EventHandler? StateChanged { add { } remove { } }
            public void Dispose() => _onDispose();
        }
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
