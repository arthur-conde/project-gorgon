using Arda.Abstractions.Logs;
using Arda.Composition.Events;
using Arda.World.Player.Events;
using FluentAssertions;
using Mithril.GameReports;
using Mithril.Reference.Models.Items;
using Mithril.TestSupport;
using Xunit;

namespace Arda.Inventory.Tests;

public class InventoryLedgerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDomainEventBus _bus = new();
    private readonly FakeLedgerStateView _view = new();
    private readonly FakeActiveCharacterService _activeChar = new();
    private readonly InventoryLedger _ledger;

    public InventoryLedgerTests()
    {
        _activeChar.ActiveCharacterName = "Alice";
        _activeChar.ActiveServer = "Alpha";

        _ledger = new InventoryLedger(
            _bus, _view, _activeChar,
            refData: null,
            dispatch: a => a());
    }

    public void Dispose() => _ledger.Dispose();

    private static LogLineMetadata Meta(DateTimeOffset ts) =>
        new(Timestamp: ts, ReadOn: ts, IsReplay: false);

    // ── Source 1: Player.log upsert ────────────────────────────────────────

    [Fact]
    public void AddItem_CreatesEntry()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));

        _ledger.Items.Should().ContainSingle();
        var item = _ledger.Items[0];
        item.InstanceId.Should().Be(1001);
        item.InternalName.Should().Be("item_sword");
        item.StackSize.Should().Be(1);
        item.Sources.Should().HaveFlag(InventorySource.PlayerLog);
        item.IsRemoved.Should().BeFalse();
    }

    [Fact]
    public void AddSameInstanceTwice_Upserts_DoesNotDuplicate()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0.AddSeconds(1))));

        _ledger.Items.Should().ContainSingle();
    }

    [Fact]
    public void UpdateItem_ChangesStackSize()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_arrow", Meta(T0)));
        _bus.Publish(new InventoryItemUpdated(1001, 5, 1, Meta(T0.AddSeconds(1))));

        _ledger.Items[0].StackSize.Should().Be(5);
    }

    [Fact]
    public void UpdateItem_UnknownInstance_Ignored()
    {
        _bus.Publish(new InventoryItemUpdated(9999, 5, 1, Meta(T0)));

        _ledger.Items.Should().BeEmpty();
    }

    // ── Soft delete ────────────────────────────────────────────────────────

    [Fact]
    public void RemoveItem_SoftDeletes()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemRemoved(1001, "item_sword", Meta(T0.AddSeconds(1))));

        _ledger.Items.Should().ContainSingle();
        var item = _ledger.Items[0];
        item.IsRemoved.Should().BeTrue();
        item.RemovedAt.Should().NotBeNull();
    }

    [Fact]
    public void RemoveItem_UnknownInstance_Ignored()
    {
        _bus.Publish(new InventoryItemRemoved(9999, "item_sword", Meta(T0)));

        _ledger.Items.Should().BeEmpty();
    }

    [Fact]
    public void ReAddRemovedItem_ClearsSoftDelete()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemRemoved(1001, "item_sword", Meta(T0.AddSeconds(1))));
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0.AddSeconds(2))));

        _ledger.Items.Should().ContainSingle();
        _ledger.Items[0].IsRemoved.Should().BeFalse();
        _ledger.Items[0].RemovedAt.Should().BeNull();
    }

    // ── Source 2: Chat resolved enrichment ─────────────────────────────────

    [Fact]
    public void Resolved_EnrichesDisplayName()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _bus.Publish(new InventoryItemResolved(1001, "item_sword", "Iron Sword", 1, Meta(T0)));

        _ledger.Items[0].DisplayName.Should().Be("Iron Sword");
        _ledger.Items[0].Sources.Should().HaveFlag(InventorySource.ChatLog);
    }

    [Fact]
    public void Resolved_UpdatesStackSize()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_arrow", Meta(T0)));
        _bus.Publish(new InventoryItemResolved(1001, "item_arrow", "Arrow", 25, Meta(T0)));

        _ledger.Items[0].StackSize.Should().Be(25);
    }

    // ── Idempotency ────────────────────────────────────────────────────────

    [Fact]
    public void ReplayAdd_OnPersistedEntry_DoesNotDuplicate()
    {
        _view.Current = new InventoryLedgerState
        {
            Entries = new Dictionary<long, PersistedItem>
            {
                [1001] = new()
                {
                    InternalName = "item_sword",
                    StackSize = 1,
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0,
                    Sources = InventorySource.PlayerLog,
                },
            },
        };
        _view.SwitchCharacter(_view.Current);

        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));

        _ledger.Items.Should().ContainSingle();
    }

    [Fact]
    public void ReplayRemove_OnAlreadySoftDeleted_DoesNotDuplicate()
    {
        _view.Current = new InventoryLedgerState
        {
            Entries = new Dictionary<long, PersistedItem>
            {
                [1001] = new()
                {
                    InternalName = "item_sword",
                    StackSize = 1,
                    RemovedAt = T0.AddSeconds(5),
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0.AddSeconds(5),
                    Sources = InventorySource.PlayerLog,
                },
            },
        };
        _view.SwitchCharacter(_view.Current);

        _bus.Publish(new InventoryItemRemoved(1001, "item_sword", Meta(T0.AddSeconds(5))));

        _ledger.Items.Should().ContainSingle();
        _ledger.Items[0].IsRemoved.Should().BeTrue();
    }

    // ── Persistence ────────────────────────────────────────────────────────

    [Fact]
    public void Flush_ProjectsToState()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _ledger.Flush();

        _view.Current!.Entries.Should().ContainKey(1001);
        var persisted = _view.Current.Entries[1001];
        persisted.InternalName.Should().Be("item_sword");
        persisted.StackSize.Should().Be(1);
        _view.SaveCount.Should().Be(1);
    }

    [Fact]
    public void Flush_WhenNotDirty_DoesNotSave()
    {
        _ledger.Flush();

        _view.SaveCount.Should().Be(0);
    }

    // ── Character switch ───────────────────────────────────────────────────

    [Fact]
    public void CharacterSwitch_HydratesFromNewState()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _ledger.Items.Should().ContainSingle();

        var newState = new InventoryLedgerState
        {
            Entries = new Dictionary<long, PersistedItem>
            {
                [2001] = new()
                {
                    InternalName = "item_shield",
                    DisplayName = "Wooden Shield",
                    StackSize = 1,
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0,
                    Sources = InventorySource.PlayerLog,
                },
                [2002] = new()
                {
                    InternalName = "item_potion",
                    StackSize = 3,
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0,
                    Sources = InventorySource.PlayerLog,
                },
            },
        };

        _view.SwitchCharacter(newState);

        _ledger.Items.Should().HaveCount(2);
        _ledger.Items.Should().Contain(m => m.InstanceId == 2001 && m.DisplayName == "Wooden Shield");
        _ledger.Items.Should().Contain(m => m.InstanceId == 2002 && m.StackSize == 3);
    }

    [Fact]
    public void CharacterSwitch_ToNullState_ClearsCollection()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _view.SwitchCharacter(null);

        _ledger.Items.Should().BeEmpty();
    }

    // ── Retention sweep ────────────────────────────────────────────────────

    [Fact]
    public void SweepRetention_RemovesExpiredEntries()
    {
        var state = new InventoryLedgerState
        {
            Entries = new Dictionary<long, PersistedItem>
            {
                [1] = new()
                {
                    InternalName = "item_old",
                    RemovedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(60),
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0,
                },
                [2] = new()
                {
                    InternalName = "item_recent",
                    RemovedAt = DateTimeOffset.UtcNow - TimeSpan.FromDays(5),
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0,
                },
                [3] = new()
                {
                    InternalName = "item_active",
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0,
                },
            },
        };

        InventoryLedger.SweepRetention(state, TimeSpan.FromDays(30));

        state.Entries.Should().HaveCount(2);
        state.Entries.Should().ContainKey(2);
        state.Entries.Should().ContainKey(3);
        state.Entries.Should().NotContainKey(1);
    }

    [Fact]
    public void SweepRetention_DoesNotRemoveActiveItems()
    {
        var state = new InventoryLedgerState
        {
            Entries = new Dictionary<long, PersistedItem>
            {
                [1] = new()
                {
                    InternalName = "item_alive",
                    FirstSeenAt = T0,
                    LastUpdatedAt = T0,
                },
            },
        };

        InventoryLedger.SweepRetention(state, TimeSpan.FromDays(1));

        state.Entries.Should().ContainSingle();
    }

    // ── Storage report reconciliation ──────────────────────────────────────

    [Fact]
    public void StorageReport_EnrichesExistingItems()
    {
        var refData = new FakeReferenceData();
        refData.Items.Should().BeEmpty();

        using var ledger = CreateLedgerWithRefData(out var bus, out var view, out var activeChar);

        bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));

        activeChar.ActiveStorageContents = new StorageReport(
            "Alice", "Alpha", T0.AddMinutes(5).ToString("O"), "Storage", 1,
            [new StorageItem(5010, "Iron Sword", 1, 100m, null, "Common", "MainHand", 10,
                IsInInventory: true, IsCrafted: false, AttunedTo: null, Crafter: null,
                Durability: null, TransmuteCount: null, CraftPoints: null, TSysPowers: null,
                TSysImbuePower: null, TSysImbuePowerTier: null, PetHusbandryState: null)]);

        activeChar.RaiseStorageReportsChanged();

        var item = ledger.Items[0];
        item.TypeId.Should().Be(5010);
        item.DisplayName.Should().Be("Iron Sword");
        item.Sources.Should().HaveFlag(InventorySource.StorageReport);
    }

    [Fact]
    public void StorageReport_SoftDeletesMissingItems()
    {
        using var ledger = CreateLedgerWithRefData(out var bus, out var view, out var activeChar);

        bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));

        activeChar.ActiveStorageContents = new StorageReport(
            "Alice", "Alpha", T0.AddMinutes(5).ToString("O"), "Storage", 1, []);

        activeChar.RaiseStorageReportsChanged();

        ledger.Items[0].IsRemoved.Should().BeTrue();
    }

    [Fact]
    public void StorageReport_SkipsDuplicateTimestamp()
    {
        using var ledger = CreateLedgerWithRefData(out var bus, out var view, out var activeChar);

        bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));

        var reportTime = T0.AddMinutes(5).ToString("O");
        view.Current!.LastStorageReportTimestamp = DateTimeOffset.Parse(reportTime);

        activeChar.ActiveStorageContents = new StorageReport(
            "Alice", "Alpha", reportTime, "Storage", 1, []);

        activeChar.RaiseStorageReportsChanged();

        ledger.Items[0].IsRemoved.Should().BeFalse("duplicate report should be skipped");
    }

    // ── Dispose ────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_FlushesAndStopsSubscriptions()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));
        _ledger.Dispose();

        _view.SaveCount.Should().Be(1, "dirty state should be flushed on dispose");

        _bus.Publish(new InventoryItemAdded(2002, "item_axe", Meta(T0)));
        _ledger.Items.Should().ContainSingle("subscriptions should be stopped");
    }

    // ── Collection change notifications ────────────────────────────────────

    [Fact]
    public void AddItem_RaisesCollectionChanged()
    {
        var changeCount = 0;
        _ledger.Items.CollectionChanged += (_, _) => changeCount++;

        _bus.Publish(new InventoryItemAdded(1001, "item_sword", Meta(T0)));

        changeCount.Should().Be(1);
    }

    [Fact]
    public void PropertyChanged_FiresOnStackSizeUpdate()
    {
        _bus.Publish(new InventoryItemAdded(1001, "item_arrow", Meta(T0)));

        var propNames = new List<string>();
        _ledger.Items[0].PropertyChanged += (_, e) => propNames.Add(e.PropertyName!);

        _bus.Publish(new InventoryItemUpdated(1001, 10, 1, Meta(T0.AddSeconds(1))));

        propNames.Should().Contain("StackSize");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static InventoryLedger CreateLedgerWithRefData(
        out TestDomainEventBus bus,
        out FakeLedgerStateView view,
        out FakeActiveCharacterService activeChar)
    {
        bus = new TestDomainEventBus();
        view = new FakeLedgerStateView();
        activeChar = new FakeActiveCharacterService
        {
            ActiveCharacterName = "Alice",
            ActiveServer = "Alpha",
        };

        var refData = new FakeReferenceData();
        var items = (Dictionary<long, Item>)refData.Items;
        var byName = (Dictionary<string, Item>)refData.ItemsByInternalName;
        var item = new Item { Id = 5010, InternalName = "item_sword", Name = "Iron Sword", IconId = 42 };
        items[5010] = item;
        byName["item_sword"] = item;

        return new InventoryLedger(bus, view, activeChar, refData, dispatch: a => a());
    }
}
