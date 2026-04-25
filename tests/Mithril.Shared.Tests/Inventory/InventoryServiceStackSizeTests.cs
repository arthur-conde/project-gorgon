using System.Threading.Channels;
using FluentAssertions;
using Mithril.Shared.Inventory;
using Mithril.Shared.Logging;
using Mithril.Shared.Reference;
using Xunit;

namespace Mithril.Shared.Tests.Inventory;

/// <summary>
/// Stack-size tracking added in the live-tracker change. Test scenarios are drawn
/// directly from the controlled 2026-04-25 experiments (50-skull gift, Guava
/// drop/pickup/split/merge/vault, loot-drop verification) — see the Arwen
/// calibration plan for the empirical data.
/// </summary>
public sealed class InventoryServiceStackSizeTests
{
    [Fact]
    public async Task AddItem_DefaultsToSizeOne_WhenNoChatCorrelation()
    {
        var stream = new ScriptedPlayerStream("[00:00:01] LocalPlayer: ProcessAddItem(BirdEgg(100), -1, True)");
        var svc = new InventoryService(stream);
        await RunAsync(svc, stream);

        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(1);
    }

    [Fact]
    public async Task UpdateItemCode_DecodesPostEventStackSize()
    {
        // Code = ((stackSize - 1) << 16) | TypeID. Verified empirically against Guava
        // (TypeID 5312): code 70848 → size 2, 136384 → size 3, 201920 → size 4.
        var stream = new ScriptedPlayerStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 70848, True)",
            "[00:00:03] LocalPlayer: ProcessUpdateItemCode(100, 136384, True)",
            "[00:00:04] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)");
        var svc = new InventoryService(stream);
        await RunAsync(svc, stream);

        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(4);
    }

    [Fact]
    public async Task UpdateItemCode_OnUnknownInstance_IsIgnored()
    {
        var stream = new ScriptedPlayerStream(
            "[00:00:01] LocalPlayer: ProcessUpdateItemCode(999, 136384, True)");
        var svc = new InventoryService(stream);
        await RunAsync(svc, stream);

        svc.TryGetStackSize(999, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Split_TracksOriginalAndNewInstanceSeparately()
    {
        // Split Guava 4 → 3 + 1 emits UpdateItemCode (size 3) on the original, then
        // AddItem with a new InstanceId for the split-off 1.
        var stream = new ScriptedPlayerStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 201920, True)", // grow to 4
            "[00:00:03] LocalPlayer: ProcessUpdateItemCode(100, 136384, False)", // split: now 3
            "[00:00:03] LocalPlayer: ProcessAddItem(Guava(101), -1, True)"); // split-off
        var svc = new InventoryService(stream);
        await RunAsync(svc, stream);

        svc.TryGetStackSize(100, out var origSize).Should().BeTrue();
        origSize.Should().Be(3);
        svc.TryGetStackSize(101, out var splitSize).Should().BeTrue();
        splitSize.Should().Be(1);
    }

    [Fact]
    public async Task DeleteItem_RetainsLastKnownStackSize()
    {
        // Arwen's gift-attribution path needs the pre-delete size: the Delete
        // line and the Calibration lookup race, and the entry must remain readable.
        var stream = new ScriptedPlayerStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(GiantSkull(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 3211264, True)", // size 50
            "[00:00:03] LocalPlayer: ProcessDeleteItem(100)");
        var svc = new InventoryService(stream);
        await RunAsync(svc, stream);

        // Entry retained with name AND size after delete.
        svc.TryResolve(100, out var name).Should().BeTrue();
        name.Should().Be("GiantSkull");
        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(50);
    }

    [Fact]
    public async Task RemoveFromStorageVault_SetsLiteralSizeOnPairedAddItem()
    {
        // Vault withdrawal into empty bag: AddItem then RemoveFromStorageVault carrying
        // the literal size N. Empirically observed: ProcessAddItem(Guava(106983847), 116, True)
        // followed by ProcessRemoveFromStorageVault(-131, -1, 106983847, 46).
        var stream = new ScriptedPlayerStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), 116, True)",
            "[00:00:01] LocalPlayer: ProcessRemoveFromStorageVault(-131, -1, 100, 46)");
        var svc = new InventoryService(stream);
        await RunAsync(svc, stream);

        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(46);
    }

    [Fact]
    public async Task RemoveFromStorageVault_OnUnknownInstance_IsIgnored()
    {
        // Saddlebag-pull-with-merge cases fire UpdateItemCode on the bag-side InstanceId
        // and RemoveFromStorageVault on the SOURCE-vault-side InstanceId (which we don't
        // track). Make sure the orphaned vault-side event doesn't pollute our map.
        var stream = new ScriptedPlayerStream(
            "[00:00:01] LocalPlayer: ProcessAddItem(Guava(100), -1, True)",
            "[00:00:02] LocalPlayer: ProcessUpdateItemCode(100, 2757824, True)", // size 43 from bag merge
            "[00:00:02] LocalPlayer: ProcessRemoveFromStorageVault(4368409, -1, 999, 42)"); // vault id we don't know
        var svc = new InventoryService(stream);
        await RunAsync(svc, stream);

        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(43);
        svc.TryGetStackSize(999, out _).Should().BeFalse();
    }

    [Fact]
    public async Task ChatStatus_BeforeAddItem_SizesNextAddItemOfSameInternalName()
    {
        // Chat status arrives before its ProcessAddItem within the correlation window.
        var ts = new DateTime(2026, 4, 25, 14, 10, 48, DateTimeKind.Utc);
        var playerStream = new ScriptedPlayerStream();
        var chatStream = new ScriptedPlayerStream();
        var refData = new FakeRefData(("Phlogiston1", "Shoddy Phlogiston"));
        var svc = new InventoryService(playerStream, diag: null, chatStream: chatStream, refData: refData);
        var run = svc.StartAsync(CancellationToken.None);

        // Chat first.
        chatStream.Push(new RawLogLine(ts, "26-04-25 15:10:48\t[Status] Shoddy Phlogiston x5 added to inventory."));
        await chatStream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        // Then AddItem.
        playerStream.Push(new RawLogLine(ts, "[14:10:48] LocalPlayer: ProcessAddItem(Phlogiston1(100), -1, True)"));
        await playerStream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(5);

        await svc.StopAsync(CancellationToken.None);
        _ = run;
    }

    [Fact]
    public async Task ChatStatus_AfterAddItem_BackFillsMostRecentPendingAdd()
    {
        // Reverse arrival order: AddItem fires first (defaults size to 1), chat lands
        // moments later and back-fills the size.
        var ts = new DateTime(2026, 4, 25, 14, 10, 48, DateTimeKind.Utc);
        var playerStream = new ScriptedPlayerStream();
        var chatStream = new ScriptedPlayerStream();
        var refData = new FakeRefData(("Phlogiston1", "Shoddy Phlogiston"));
        var svc = new InventoryService(playerStream, diag: null, chatStream: chatStream, refData: refData);
        var run = svc.StartAsync(CancellationToken.None);

        playerStream.Push(new RawLogLine(ts, "[14:10:48] LocalPlayer: ProcessAddItem(Phlogiston1(100), -1, True)"));
        await playerStream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        // Without chat yet, size defaults to 1.
        svc.TryGetStackSize(100, out var preChatSize).Should().BeTrue();
        preChatSize.Should().Be(1);

        chatStream.Push(new RawLogLine(ts, "26-04-25 15:10:48\t[Status] Shoddy Phlogiston x5 added to inventory."));
        await chatStream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        svc.TryGetStackSize(100, out var postChatSize).Should().BeTrue();
        postChatSize.Should().Be(5);

        await svc.StopAsync(CancellationToken.None);
        _ = run;
    }

    [Fact]
    public async Task NonStatusChatLines_AreIgnored()
    {
        var ts = new DateTime(2026, 4, 25, 14, 10, 48, DateTimeKind.Utc);
        var playerStream = new ScriptedPlayerStream();
        var chatStream = new ScriptedPlayerStream();
        var refData = new FakeRefData(("Phlogiston1", "Shoddy Phlogiston"));
        var svc = new InventoryService(playerStream, diag: null, chatStream: chatStream, refData: refData);
        var run = svc.StartAsync(CancellationToken.None);

        chatStream.Push(new RawLogLine(ts, "26-04-25 15:10:48\t[Trade] Joltknocker: wtb 1 phoenix egg 25k"));
        chatStream.Push(new RawLogLine(ts, "26-04-25 15:10:48\t[Status] Guild status changed."));
        await chatStream.WaitForDrainAsync(TimeSpan.FromSeconds(2));
        playerStream.Push(new RawLogLine(ts, "[14:10:48] LocalPlayer: ProcessAddItem(Phlogiston1(100), -1, True)"));
        await playerStream.WaitForDrainAsync(TimeSpan.FromSeconds(2));

        // No correlation found → defaults to 1.
        svc.TryGetStackSize(100, out var size).Should().BeTrue();
        size.Should().Be(1);

        await svc.StopAsync(CancellationToken.None);
        _ = run;
    }

    private static async Task RunAsync(InventoryService svc, ScriptedPlayerStream stream)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var runTask = svc.StartAsync(cts.Token);
        await stream.WaitForDrainAsync(cts.Token);
        await cts.CancelAsync();
        try { await svc.StopAsync(CancellationToken.None); } catch (OperationCanceledException) { }
        _ = runTask;
    }

    /// <summary>
    /// Reusable scripted stream that satisfies both <see cref="IPlayerLogStream"/> and
    /// <see cref="IChatLogStream"/> (same shape — both yield <see cref="RawLogLine"/>).
    /// </summary>
    private sealed class ScriptedPlayerStream : IPlayerLogStream, IChatLogStream
    {
        private readonly Channel<RawLogLine> _channel = Channel.CreateUnbounded<RawLogLine>();
        private long _pending;
        private TaskCompletionSource _drained = NewDrainTcs();

        public ScriptedPlayerStream() { _drained.TrySetResult(); }

        public ScriptedPlayerStream(params string[] lines)
            : this(lines.Select(l => new RawLogLine(DateTime.UtcNow, l)).ToArray()) { }

        public ScriptedPlayerStream(RawLogLine[] lines)
        {
            if (lines.Length == 0)
            {
                _drained.TrySetResult();
                return;
            }
            Interlocked.Add(ref _pending, lines.Length);
            foreach (var line in lines) _channel.Writer.TryWrite(line);
        }

        public void Push(RawLogLine line)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _drained, NewDrainTcs());
            _channel.Writer.TryWrite(line);
        }

        public Task WaitForDrainAsync(CancellationToken ct) => _drained.Task.WaitAsync(ct);
        public Task WaitForDrainAsync(TimeSpan timeout) => _drained.Task.WaitAsync(timeout);

        public async IAsyncEnumerable<RawLogLine> SubscribeAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            while (await _channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var line))
                {
                    yield return line;
                    if (Interlocked.Decrement(ref _pending) == 0)
                        _drained.TrySetResult();
                }
            }
        }

        private static TaskCompletionSource NewDrainTcs() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Minimal <see cref="IReferenceDataService"/> stub — only <see cref="ItemsByInternalName"/>
    /// is consulted by InventoryService for chat-name → InternalName resolution.
    /// </summary>
    private sealed class FakeRefData : IReferenceDataService
    {
        private readonly Dictionary<string, ItemEntry> _byName;

        public FakeRefData(params (string InternalName, string DisplayName)[] items)
        {
            _byName = items.ToDictionary(
                t => t.InternalName,
                t => new ItemEntry(0, t.DisplayName, t.InternalName, MaxStackSize: 100, IconId: 0, Keywords: []),
                StringComparer.Ordinal);
        }

        public IReadOnlyList<string> Keys { get; } = ["items"];
        public IReadOnlyDictionary<long, ItemEntry> Items { get; } = new Dictionary<long, ItemEntry>();
        public IReadOnlyDictionary<string, ItemEntry> ItemsByInternalName => _byName;
        public IReadOnlyDictionary<string, RecipeEntry> Recipes { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, RecipeEntry> RecipesByInternalName { get; } = new Dictionary<string, RecipeEntry>();
        public IReadOnlyDictionary<string, SkillEntry> Skills { get; } = new Dictionary<string, SkillEntry>();
        public IReadOnlyDictionary<string, XpTableEntry> XpTables { get; } = new Dictionary<string, XpTableEntry>();
        public IReadOnlyDictionary<string, NpcEntry> Npcs { get; } = new Dictionary<string, NpcEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<ItemSource>> ItemSources { get; } = new Dictionary<string, IReadOnlyList<ItemSource>>();
        public IReadOnlyDictionary<string, AttributeEntry> Attributes { get; } = new Dictionary<string, AttributeEntry>();
        public IReadOnlyDictionary<string, PowerEntry> Powers { get; } = new Dictionary<string, PowerEntry>();
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Profiles { get; } = new Dictionary<string, IReadOnlyList<string>>();
        public ReferenceFileSnapshot GetSnapshot(string key) => new(key, ReferenceFileSource.Bundled, "test", null, 0);
        public Task RefreshAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task RefreshAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void BeginBackgroundRefresh() { }
        public event EventHandler<string>? FileUpdated { add { } remove { } }
    }
}
